using Dicerio.Engine;

namespace Dicerio.Server.Hubs;

public sealed record CreateRoomRequest(string? DisplayName, int? TargetScore);

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
    int? PendingLockHintTotal,
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
        var bestUnlockedScore = ComputeBestUnlockedScore(s);
        for (var i = 0; i < s.Dice.Count; i++)
        {
            dice.Add(new DiceDto(s.Dice[i].Value, s.Dice[i].Locked, null, null));
        }

        var elapsedMs = (long)Math.Max(0, (DateTime.UtcNow - s.StartedAtUtc).TotalMilliseconds);

        return new MatchStateDto(
            MatchId: s.MatchId,
            RoomCode: s.RoomCode,
            Rules: new RuleSetDto(s.Rules.Id, s.Rules.TargetScore, s.Rules.DiceCount, s.Rules.HotDiceReroll, s.Rules.AllowStraights),
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
            PendingLockHintTotal: bestUnlockedScore,
            ActivePlayerPendingLockIndexes: activePlayerPendingLockIndexes);
    }

    private static int? ComputeBestUnlockedScore(MatchState s)
    {
        if (s.Phase != MatchPhase.AwaitingLock)
        {
            return null;
        }

        var faces = s.Dice.Where(d => !d.Locked && d.Value != 0).Select(d => d.Value).ToArray();
        if (faces.Length == 0)
        {
            return null;
        }

        var best = 0;
        for (var mask = 1; mask < (1 << faces.Length); mask++)
        {
            var picked = new List<int>(faces.Length);
            for (var i = 0; i < faces.Length; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    picked.Add(faces[i]);
                }
            }

            var score = Scoring.ScoreLock(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(picked));
            if (score is not null && score.Points > best)
            {
                best = score.Points;
            }
        }

        return best == 0 ? null : best;
    }
}
