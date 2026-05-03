namespace Dicerio.Engine;

public enum EngineErrorCode
{
    NotYourTurn,
    WrongPhase,
    EmptyLock,
    InvalidLockIndex,
    InvalidLockPartition,
    MatchNotStarted,
    MatchAlreadyOver,
    UnknownPlayer,
}

public sealed record EngineError(EngineErrorCode Code, string Message);

public readonly record struct EngineResult(MatchState? State, EngineError? Error)
{
    public bool IsOk => Error is null;

    public static EngineResult Ok(MatchState state) => new(state, null);
    public static EngineResult Fail(EngineErrorCode code, string message) => new(null, new EngineError(code, message));
}
