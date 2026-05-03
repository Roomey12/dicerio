using Dicerio.Engine;

namespace Dicerio.Server.Hubs;

public sealed record CreateRoomRequest(string? DisplayName, int? TargetScore, int? MaxPlayers);

public sealed record JoinRoomRequest(string RoomCode, string? DisplayName);

public sealed record CreateRoomResult(string RoomCode, string MatchId, string PlayerId);

public sealed record JoinRoomResult(string RoomCode, string MatchId, string PlayerId);

public sealed record HubErrorPayload(string Code, string Message);

public sealed record DiceDto(int Value, bool Locked, int? HintPoints, string? HintLabel);

public sealed record PlayerDto(string PlayerId, string DisplayName, int Seat, int MatchScore, bool Connected);

public sealed record RuleSetDto(string Id, int TargetScore, int DiceCount, bool HotDiceReroll, bool AllowStraights);

public sealed record LastEventDto(string Kind, string? PlayerId, int? Points, string? Message);

public sealed record MatchStateDto(
    string MatchId,
    string RoomCode,
    RuleSetDto Rules,
    int MaxPlayers,
    string? HostPlayerId,
    IReadOnlyList<PlayerDto> Players,
    string? ActivePlayerId,
    string Phase,
    IReadOnlyList<DiceDto> Dice,
    int TurnScore,
    string? WinnerId,
    LastEventDto LastEvent,
    long Version,
    long ElapsedMs,
    string? YouAre,
    IReadOnlyList<int>? ActivePlayerPendingLockIndexes);

public static class Mapper
{
    public static MatchStateDto Project(
        MatchState s,
        string? requesterPlayerId,
        IReadOnlyDictionary<string, string?> connections,
        IReadOnlyList<int>? activePlayerPendingLockIndexes)
    {
        var players = s.Players
            .Select(p => new PlayerDto(
                p.PlayerId,
                p.DisplayName,
                p.Seat,
                p.MatchScore,
                connections.TryGetValue(p.PlayerId, out var cid) && cid != null))
            .ToList();

        var dice = new List<DiceDto>(s.Dice.Count);
        for (var i = 0; i < s.Dice.Count; i++)
        {
            dice.Add(new DiceDto(s.Dice[i].Value, s.Dice[i].Locked, null, null));
        }

        var elapsedMs = (long)Math.Max(0, (DateTime.UtcNow - s.StartedAtUtc).TotalMilliseconds);

        return new MatchStateDto(
            MatchId: s.MatchId,
            RoomCode: s.RoomCode,
            Rules: new RuleSetDto(s.Rules.Id, s.Rules.TargetScore, s.Rules.DiceCount, s.Rules.HotDiceReroll, s.Rules.AllowStraights),
            MaxPlayers: s.MaxPlayers,
            HostPlayerId: s.Players.Count > 0 ? s.Players[0].PlayerId : null,
            Players: players,
            ActivePlayerId: s.ActivePlayerId,
            Phase: s.Phase.ToString(),
            Dice: dice,
            TurnScore: s.TurnScore,
            WinnerId: s.WinnerId,
            LastEvent: new LastEventDto(s.LastEvent.Kind.ToString(), s.LastEvent.PlayerId, s.LastEvent.Points, s.LastEvent.Message),
            Version: s.Version,
            ElapsedMs: elapsedMs,
            YouAre: requesterPlayerId,
            ActivePlayerPendingLockIndexes: activePlayerPendingLockIndexes);
    }
}
