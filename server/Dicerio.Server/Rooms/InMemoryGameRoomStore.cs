using System.Collections.Concurrent;
using Dicerio.Engine;

namespace Dicerio.Server.Rooms;

public sealed class InMemoryGameRoomStore : IGameRoomStore
{
    private sealed class Entry
    {
        public required MatchState State { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public Dictionary<string, string?> ConnectionByPlayer { get; } = new();

        /// <summary>
        /// Active player's in-progress lock selection (die indices) before SubmitLock.
        /// Ephemeral — not part of <see cref="MatchState"/>; cleared on every real move.
        /// </summary>
        public int[]? PendingLockSelection { get; set; }

        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _byMatchId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _matchIdByCode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RoomMembership> _membershipByConnection = new(StringComparer.Ordinal);

    public IEnumerable<MatchState> Snapshot()
    {
        foreach (var entry in _byMatchId.Values)
        {
            lock (entry.Gate)
            {
                yield return entry.State;
            }
        }
    }

    public Task<MatchState> CreateAsync(MatchState initialState, string hostConnectionId, CancellationToken ct = default)
    {
        var entry = new Entry { State = initialState, LastActivityUtc = DateTime.UtcNow };
        if (!_byMatchId.TryAdd(initialState.MatchId, entry))
        {
            throw new InvalidOperationException($"Match {initialState.MatchId} already exists.");
        }

        if (!_matchIdByCode.TryAdd(initialState.RoomCode, initialState.MatchId))
        {
            _byMatchId.TryRemove(initialState.MatchId, out _);
            throw new InvalidOperationException($"Room code {initialState.RoomCode} already in use.");
        }

        var hostPlayer = initialState.Players[0];
        entry.ConnectionByPlayer[hostPlayer.PlayerId] = hostConnectionId;
        _membershipByConnection[hostConnectionId] = new RoomMembership(initialState.MatchId, hostPlayer.PlayerId, initialState.RoomCode);

        return Task.FromResult(initialState);
    }

    public Task<MatchState?> FindByCodeAsync(string roomCode, CancellationToken ct = default)
    {
        if (!_matchIdByCode.TryGetValue(roomCode, out var matchId))
        {
            return Task.FromResult<MatchState?>(null);
        }

        return FindByMatchIdAsync(matchId, ct);
    }

    public Task<MatchState?> FindByMatchIdAsync(string matchId, CancellationToken ct = default)
    {
        if (!_byMatchId.TryGetValue(matchId, out var entry))
        {
            return Task.FromResult<MatchState?>(null);
        }

        lock (entry.Gate)
        {
            return Task.FromResult<MatchState?>(entry.State);
        }
    }

    public Task<MatchState?> UpdateAsync(string matchId, Func<MatchState, MatchState> mutator, CancellationToken ct = default)
    {
        if (!_byMatchId.TryGetValue(matchId, out var entry))
        {
            return Task.FromResult<MatchState?>(null);
        }

        lock (entry.Gate)
        {
            var next = mutator(entry.State);
            entry.State = next;
            entry.LastActivityUtc = DateTime.UtcNow;
            return Task.FromResult<MatchState?>(next);
        }
    }

    public Task<bool> SetConnectionAsync(string matchId, string playerId, string? connectionId, CancellationToken ct = default)
    {
        if (!_byMatchId.TryGetValue(matchId, out var entry))
        {
            return Task.FromResult(false);
        }

        lock (entry.Gate)
        {
            if (!entry.State.Players.Any(p => p.PlayerId == playerId))
            {
                return Task.FromResult(false);
            }

            if (entry.ConnectionByPlayer.TryGetValue(playerId, out var prev) && prev != null && prev != connectionId)
            {
                _membershipByConnection.TryRemove(prev, out _);
            }

            entry.ConnectionByPlayer[playerId] = connectionId;
            entry.LastActivityUtc = DateTime.UtcNow;

            if (connectionId != null)
            {
                _membershipByConnection[connectionId] = new RoomMembership(matchId, playerId, entry.State.RoomCode);
            }

            return Task.FromResult(true);
        }
    }

    public Task<RoomMembership?> FindByConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        _membershipByConnection.TryGetValue(connectionId, out var membership);
        return Task.FromResult<RoomMembership?>(membership);
    }

    public Task RemoveAsync(string matchId, CancellationToken ct = default)
    {
        if (!_byMatchId.TryRemove(matchId, out var entry))
        {
            return Task.CompletedTask;
        }

        lock (entry.Gate)
        {
            _matchIdByCode.TryRemove(entry.State.RoomCode, out _);
            foreach (var conn in entry.ConnectionByPlayer.Values)
            {
                if (conn != null)
                {
                    _membershipByConnection.TryRemove(conn, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    public IEnumerable<(MatchState State, DateTime LastActivityUtc, IReadOnlyDictionary<string, string?> Connections)> SnapshotEntries()
    {
        foreach (var entry in _byMatchId.Values)
        {
            lock (entry.Gate)
            {
                var connsCopy = new Dictionary<string, string?>(entry.ConnectionByPlayer);
                yield return (entry.State, entry.LastActivityUtc, connsCopy);
            }
        }
    }

    /// <summary>
    /// Validates and stores the active player's tentative lock indices for UI sync.
    /// Empty array clears the preview. Returns false if the player isn't allowed to preview.
    /// </summary>
    public bool TrySetPendingLockSelection(string matchId, string playerId, int[] indexes)
    {
        if (!_byMatchId.TryGetValue(matchId, out var entry))
        {
            return false;
        }

        lock (entry.Gate)
        {
            var s = entry.State;
            if (s.Phase != MatchPhase.AwaitingLock || s.ActivePlayerId != playerId)
            {
                return false;
            }

            if (indexes.Length == 0)
            {
                entry.PendingLockSelection = null;
                entry.LastActivityUtc = DateTime.UtcNow;
                return true;
            }

            var seen = new HashSet<int>();
            foreach (var idx in indexes)
            {
                if (idx < 0 || idx >= s.Dice.Count)
                {
                    return false;
                }

                if (!seen.Add(idx))
                {
                    return false;
                }

                if (s.Dice[idx].Locked)
                {
                    return false;
                }

                if (s.Dice[idx].Value == 0)
                {
                    return false;
                }
            }

            entry.PendingLockSelection = indexes.OrderBy(x => x).ToArray();
            entry.LastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    public void ClearPendingLockSelection(string matchId)
    {
        if (!_byMatchId.TryGetValue(matchId, out var entry))
        {
            return;
        }

        lock (entry.Gate)
        {
            entry.PendingLockSelection = null;
        }
    }

    public int[]? GetPendingLockSelectionSnapshot(string matchId)
    {
        if (!_byMatchId.TryGetValue(matchId, out var entry))
        {
            return null;
        }

        lock (entry.Gate)
        {
            return entry.PendingLockSelection?.ToArray();
        }
    }
}
