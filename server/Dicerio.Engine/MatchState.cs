namespace Dicerio.Engine;

public enum MatchPhase
{
    WaitingForOpponent,
    AwaitingRoll,
    AwaitingLock,

    /// <summary>
    /// Transient post-bust phase that holds the busted dice on screen so both
    /// clients are guaranteed to see them. Neither player can act; the hub
    /// auto-advances to <see cref="AwaitingRoll"/> for the opponent after a
    /// short delay (see <see cref="FarkleEngine.AcknowledgeBust"/>).
    /// </summary>
    BustReveal,

    GameOver,
}

public readonly record struct Die(int Value, bool Locked)
{
    public static Die Empty => new(0, false);
}

public sealed record Player(
    string PlayerId,
    string DisplayName,
    int Seat,
    int MatchScore);

/// <summary>
/// Discriminator for the most recent server event. Clients use this to drive
/// animations / toasts; the canonical match state is always derived
/// independently from this field.
/// </summary>
public enum LastEventKind
{
    None,
    PlayerJoined,
    MatchStarted,
    Rolled,
    Locked,
    Banked,
    Busted,
    HotDice,
    GameOver,
    Forfeit,
}

public sealed record LastEvent(
    LastEventKind Kind,
    string? PlayerId,
    int? Points,
    string? Message);

public sealed record MatchState(
    string MatchId,
    string RoomCode,
    RuleSet Rules,
    IReadOnlyList<Player> Players,
    string? ActivePlayerId,
    MatchPhase Phase,
    IReadOnlyList<Die> Dice,
    int TurnScore,
    string? WinnerId,
    LastEvent LastEvent,
    long Version)
{
    /// <summary>
    /// Server-side wall clock when the match was created. Used by the projection
    /// to stamp every broadcast with <c>elapsedMs</c> so both clients render
    /// identical timestamps for the same event.
    /// </summary>
    public required DateTime StartedAtUtc { get; init; }

    public Player? GetPlayer(string playerId) =>
        Players.FirstOrDefault(p => p.PlayerId == playerId);

    public Player? Opponent(string playerId) =>
        Players.FirstOrDefault(p => p.PlayerId != playerId);

    public bool IsActive(string playerId) => ActivePlayerId == playerId;
}
