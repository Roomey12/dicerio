using Dicerio.Engine;
using Dicerio.Server.Rooms;
using Microsoft.AspNetCore.SignalR;

namespace Dicerio.Server.Hubs;

public sealed class BustRevealOptions
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(1800);
}

/// <summary>
/// Wire protocol — kept thin: maps connection ids to players, validates input,
/// delegates all game logic to <see cref="FarkleEngine"/>, broadcasts
/// authoritative <see cref="MatchStateDto"/> snapshots to a per-match group.
/// </summary>
public sealed class GameHub : Hub
{
    public const string ServerEventStateUpdated = "StateUpdated";
    public const string ServerEventError = "GameError";
    public const string ServerEventMatchEnded = "MatchEnded";

    private readonly InMemoryGameRoomStore _store;
    private readonly IDiceRoller _roller;
    private readonly JoinRateLimiter _joinLimiter;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly BustRevealOptions _bustReveal;
    private readonly ILogger<GameHub> _logger;

    public GameHub(
        InMemoryGameRoomStore store,
        IDiceRoller roller,
        JoinRateLimiter joinLimiter,
        IHubContext<GameHub> hubContext,
        BustRevealOptions bustReveal,
        ILogger<GameHub> logger)
    {
        _store = store;
        _roller = roller;
        _joinLimiter = joinLimiter;
        _hubContext = hubContext;
        _bustReveal = bustReveal;
        _logger = logger;
    }

    private static string GroupName(string matchId) => $"match:{matchId}";

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var membership = await _store.FindByConnectionAsync(Context.ConnectionId);
        if (membership is not null)
        {
            var matchId = membership.MatchId;
            await _store.SetConnectionAsync(matchId, membership.PlayerId, null);
            _store.ClearPendingLockSelection(matchId);

            var state = await _store.FindByMatchIdAsync(matchId);
            if (state is { Phase: MatchPhase.WaitingForOpponent })
            {
                await HandleLobbyDisconnectAsync(matchId, membership.PlayerId, state);
            }
            else if (state is { Phase: MatchPhase.AwaitingRoll or MatchPhase.AwaitingLock })
            {
                _logger.LogInformation("Player {PlayerId} disconnected mid-match {MatchId}; forfeiting.",
                    membership.PlayerId, matchId);

                var updated = await _store.UpdateAsync(matchId, s =>
                {
                    var r = FarkleEngine.Forfeit(s, membership.PlayerId, "Player disconnected");
                    return r.IsOk ? r.State! : s;
                });

                if (updated is not null)
                {
                    await BroadcastState(updated);
                }
            }
            else if (state is not null)
            {
                await BroadcastState(state);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public const int MinTargetScore = 100;
    public const int MaxTargetScore = 100_000;

    public async Task<CreateRoomResult> CreateRoom(CreateRoomRequest request)
    {
        var displayName = SanitizeName(request.DisplayName) ?? "Host";
        var rules = RuleSet.V1;
        if (request.TargetScore is int target)
        {
            if (target < MinTargetScore || target > MaxTargetScore)
            {
                throw new HubException($"InvalidTargetScore: must be between {MinTargetScore} and {MaxTargetScore:N0}.");
            }

            rules = rules.WithTargetScore(target);
        }

        string roomCode;
        for (var attempt = 0; ; attempt++)
        {
            roomCode = RoomCode.Generate();
            if (await _store.FindByCodeAsync(roomCode) is null)
            {
                break;
            }

            if (attempt > 8)
            {
                throw new HubException("Failed to allocate a unique room code; please retry.");
            }
        }

        var maxPlayers = request.MaxPlayers is int mp
            ? Math.Clamp(mp, FarkleEngine.MinPlayers, FarkleEngine.MaxPlayersAllowed)
            : FarkleEngine.MinPlayers;

        var matchId = Guid.NewGuid().ToString("n");
        var hostPlayerId = $"p-{Guid.NewGuid():n}";
        var host = new Player(hostPlayerId, displayName, Seat: 0, MatchScore: 0);

        var initial = FarkleEngine.CreateMatch(matchId, roomCode, rules, host, maxPlayers);
        await _store.CreateAsync(initial, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(matchId));

        await Clients.Caller.SendAsync(ServerEventStateUpdated,
            ProjectDto(matchId, initial, hostPlayerId));

        return new CreateRoomResult(roomCode, matchId, hostPlayerId);
    }

    public async Task<JoinRoomResult> JoinRoom(JoinRoomRequest request)
    {
        if (!_joinLimiter.TryAcquire(Context.ConnectionId))
        {
            throw new HubException("RateLimited: too many join attempts; slow down.");
        }

        var normalized = RoomCode.Normalize(request.RoomCode);
        if (normalized is null)
        {
            throw new HubException($"InvalidCode: room code must be {RoomCode.Length} characters from the unambiguous alphabet.");
        }

        var state = await _store.FindByCodeAsync(normalized);
        if (state is null)
        {
            throw new HubException("RoomNotFound: no room with that code.");
        }

        if (state.Phase != MatchPhase.WaitingForOpponent)
        {
            throw new HubException("GameAlreadyStarted: cannot join a match in progress.");
        }

        if (state.Players.Count >= state.MaxPlayers)
        {
            throw new HubException("RoomFull: this room is full.");
        }

        var displayName = SanitizeName(request.DisplayName) ?? "Guest";
        var guestPlayerId = $"p-{Guid.NewGuid():n}";
        var guest = new Player(guestPlayerId, displayName, Seat: state.Players.Count, MatchScore: 0);

        MatchState? updated = null;
        await _store.UpdateAsync(state.MatchId, s =>
        {
            var r = FarkleEngine.AddOpponent(s, guest, _roller);
            if (!r.IsOk)
            {
                throw new HubException($"{r.Error!.Code}: {r.Error.Message}");
            }

            updated = r.State;
            return r.State!;
        });

        if (updated is null)
        {
            throw new HubException("RoomNotFound: room disappeared while joining.");
        }

        await _store.SetConnectionAsync(updated.MatchId, guestPlayerId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(updated.MatchId));
        _store.ClearPendingLockSelection(updated.MatchId);
        await BroadcastState(updated);
        return new JoinRoomResult(updated.RoomCode, updated.MatchId, guestPlayerId);
    }

    public async Task RollAgain()
    {
        await ApplyAction((state, playerId) => FarkleEngine.Roll(state, playerId, _roller));
    }

    public async Task SubmitLock(IReadOnlyList<int> diceIndexes)
    {
        if (diceIndexes is null)
        {
            throw new HubException("InvalidArgs: diceIndexes is required.");
        }

        await ApplyAction((state, playerId) => FarkleEngine.SubmitLock(state, playerId, diceIndexes));
    }

    public async Task Bank()
    {
        await ApplyAction((state, playerId) => FarkleEngine.Bank(state, playerId));
    }

    public async Task PreviewLock(IReadOnlyList<int> diceIndexes)
    {
        if (diceIndexes is null)
        {
            throw new HubException("InvalidArgs: diceIndexes is required.");
        }

        var membership = await _store.FindByConnectionAsync(Context.ConnectionId);
        if (membership is null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload("NoMembership", "You are not currently in a match."));
            return;
        }

        var indexes = diceIndexes.ToArray();
        if (!_store.TrySetPendingLockSelection(membership.MatchId, membership.PlayerId, indexes))
        {
            return;
        }

        var state = await _store.FindByMatchIdAsync(membership.MatchId);
        if (state is null)
        {
            return;
        }

        await BroadcastState(state);
    }

    public async Task StartMatch()
    {
        var membership = await _store.FindByConnectionAsync(Context.ConnectionId);
        if (membership is null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload("NoMembership", "You are not currently in a match."));
            return;
        }

        EngineError? error = null;
        var updated = await _store.UpdateAsync(membership.MatchId, s =>
        {
            var r = FarkleEngine.StartLobbyMatch(s, membership.PlayerId, _roller);
            if (!r.IsOk)
            {
                error = r.Error;
                return s;
            }

            return r.State!;
        });

        if (updated is null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload("RoomNotFound", "Match no longer exists."));
            return;
        }

        if (error is not null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload(error.Code.ToString(), error.Message));
            return;
        }

        _store.ClearPendingLockSelection(membership.MatchId);
        await BroadcastState(updated);
    }

    public async Task PlayAgain()
    {
        var membership = await _store.FindByConnectionAsync(Context.ConnectionId);
        if (membership is null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload("NoMembership", "You are not currently in a match."));
            return;
        }

        EngineError? error = null;
        var updated = await _store.UpdateAsync(membership.MatchId, s =>
        {
            var r = FarkleEngine.Rematch(s, membership.PlayerId, _roller);
            if (!r.IsOk)
            {
                error = r.Error;
                return s;
            }

            return r.State!;
        });

        if (updated is null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload("RoomNotFound", "Match no longer exists."));
            return;
        }

        if (error is not null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload(error.Code.ToString(), error.Message));
            return;
        }

        _store.ClearPendingLockSelection(membership.MatchId);
        await BroadcastState(updated);
    }

    public async Task LeaveRoom()
    {
        var membership = await _store.FindByConnectionAsync(Context.ConnectionId);
        if (membership is null)
        {
            return;
        }

        var matchId = membership.MatchId;
        var playerId = membership.PlayerId;
        await _store.SetConnectionAsync(matchId, playerId, null);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(matchId));

        var state = await _store.FindByMatchIdAsync(matchId);
        if (state is null)
        {
            return;
        }

        if (state.Phase == MatchPhase.WaitingForOpponent)
        {
            var isHost = state.Players.Count > 0 && state.Players[0].PlayerId == playerId;
            if (isHost && state.Players.Count > 1)
            {
                await _hubContext.Clients.Group(GroupName(matchId)).SendAsync(ServerEventError,
                    new HubErrorPayload("HostLeft", "The host closed the room."));
                await TearDownMatchAsync(matchId);
                return;
            }

            if (isHost && state.Players.Count == 1)
            {
                await TearDownMatchAsync(matchId);
                return;
            }

            var left = await _store.UpdateAsync(matchId, s =>
            {
                var r = FarkleEngine.LeaveLobby(s, playerId);
                return r.IsOk ? r.State! : s;
            });

            if (left is null)
            {
                return;
            }

            if (left.Players.Count == 0)
            {
                await TearDownMatchAsync(matchId);
                return;
            }

            _store.PruneConnectionsToPlayers(matchId, left.Players.Select(p => p.PlayerId).ToList());
            _store.ClearPendingLockSelection(matchId);
            await BroadcastState(left);
            return;
        }

        var updated = await _store.UpdateAsync(matchId, s =>
        {
            if (s.Phase is MatchPhase.AwaitingRoll or MatchPhase.AwaitingLock)
            {
                var r = FarkleEngine.Forfeit(s, playerId, "Player left");
                return r.IsOk ? r.State! : s;
            }

            return s;
        });

        if (updated is not null)
        {
            _store.ClearPendingLockSelection(matchId);
            await BroadcastState(updated);
        }
    }

    private async Task ApplyAction(Func<MatchState, string, EngineResult> apply)
    {
        var membership = await _store.FindByConnectionAsync(Context.ConnectionId);
        if (membership is null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload("NoMembership", "You are not currently in a match."));
            return;
        }

        EngineError? error = null;
        var updated = await _store.UpdateAsync(membership.MatchId, s =>
        {
            var r = apply(s, membership.PlayerId);
            if (!r.IsOk)
            {
                error = r.Error;
                return s;
            }

            return r.State!;
        });

        if (updated is null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload("RoomNotFound", "Match no longer exists."));
            return;
        }

        if (error is not null)
        {
            await Clients.Caller.SendAsync(ServerEventError,
                new HubErrorPayload(error.Code.ToString(), error.Message));
            return;
        }

        _store.ClearPendingLockSelection(membership.MatchId);
        await BroadcastState(updated);

        if (updated.Phase == MatchPhase.BustReveal)
        {
            ScheduleBustAcknowledge(updated.MatchId, updated.Version);
        }
    }

    private void ScheduleBustAcknowledge(string matchId, long bustVersion)
    {
        var delay = _bustReveal.Duration;
        var logger = _logger;
        var store = _store;
        var hub = _hubContext;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);

                var updated = await store.UpdateAsync(matchId, s =>
                {
                    if (s.Phase != MatchPhase.BustReveal || s.Version != bustVersion)
                    {
                        return s;
                    }

                    var r = FarkleEngine.AcknowledgeBust(s);
                    return r.IsOk ? r.State! : s;
                });

                if (updated is null || updated.Version == bustVersion)
                {
                    return;
                }

                store.ClearPendingLockSelection(matchId);
                var connections = ConnectionsFor(matchId);
                var pending = store.GetPendingLockSelectionSnapshot(matchId);
                foreach (var (playerId, connectionId) in connections)
                {
                    if (connectionId is null)
                    {
                        continue;
                    }

                    await hub.Clients.Client(connectionId).SendAsync(
                        ServerEventStateUpdated,
                        Mapper.Project(updated, playerId, connections, pending));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to advance past BustReveal for match {MatchId}", matchId);
            }
        });
    }

    private IReadOnlyDictionary<string, string?> ConnectionsFor(string matchId)
    {
        foreach (var (s, _, conns) in _store.SnapshotEntries())
        {
            if (s.MatchId == matchId)
            {
                return conns;
            }
        }

        return new Dictionary<string, string?>();
    }

    private MatchStateDto ProjectDto(string matchId, MatchState state, string? requesterPlayerId)
    {
        var connections = ConnectionsFor(matchId);
        var pending = _store.GetPendingLockSelectionSnapshot(matchId);
        return Mapper.Project(state, requesterPlayerId, connections, pending);
    }

    private async Task BroadcastState(MatchState state)
    {
        var matchId = state.MatchId;
        var connections = ConnectionsFor(matchId);
        var pending = _store.GetPendingLockSelectionSnapshot(matchId);
        foreach (var (playerId, connectionId) in connections)
        {
            if (connectionId is null)
            {
                continue;
            }

            await Clients.Client(connectionId).SendAsync(ServerEventStateUpdated,
                Mapper.Project(state, playerId, connections, pending));
        }

        if (state.Phase == MatchPhase.GameOver)
        {
            await Clients.Group(GroupName(state.MatchId)).SendAsync(ServerEventMatchEnded, state.WinnerId);
        }
    }

    private async Task TearDownMatchAsync(string matchId)
    {
        var g = GroupName(matchId);
        foreach (var (_, cid) in ConnectionsFor(matchId).ToList())
        {
            if (cid is not null)
            {
                await _hubContext.Groups.RemoveFromGroupAsync(cid, g);
            }
        }

        await _store.RemoveAsync(matchId);
    }

    private async Task HandleLobbyDisconnectAsync(string matchId, string playerId, MatchState state)
    {
        var isHost = state.Players.Count > 0 && state.Players[0].PlayerId == playerId;
        if (isHost && state.Players.Count > 1)
        {
            await _hubContext.Clients.Group(GroupName(matchId)).SendAsync(ServerEventError,
                new HubErrorPayload("HostLeft", "The host disconnected; room closed."));
            await TearDownMatchAsync(matchId);
            return;
        }

        if (isHost && state.Players.Count == 1)
        {
            await TearDownMatchAsync(matchId);
            return;
        }

        var left = await _store.UpdateAsync(matchId, s =>
        {
            var r = FarkleEngine.LeaveLobby(s, playerId);
            return r.IsOk ? r.State! : s;
        });

        if (left is null)
        {
            return;
        }

        if (left.Players.Count == 0)
        {
            await TearDownMatchAsync(matchId);
            return;
        }

        _store.PruneConnectionsToPlayers(matchId, left.Players.Select(p => p.PlayerId).ToList());
        _store.ClearPendingLockSelection(matchId);
        await BroadcastState(left);
    }

    private static string? SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 24)
        {
            trimmed = trimmed[..24];
        }

        return trimmed;
    }
}
