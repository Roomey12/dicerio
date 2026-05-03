using Dicerio.Engine;

namespace Dicerio.Server.Rooms;

public sealed record RoomMembership(string MatchId, string PlayerId, string RoomCode);

/// <summary>
/// Persistence boundary for active matches. The MVP implementation is in-memory
/// and single-process; the contract is shaped to allow swapping in a Redis-backed
/// store with `Update` becoming an optimistic-concurrency operation later.
/// </summary>
public interface IGameRoomStore
{
    Task<MatchState> CreateAsync(MatchState initialState, string hostConnectionId, CancellationToken ct = default);

    Task<MatchState?> FindByCodeAsync(string roomCode, CancellationToken ct = default);

    Task<MatchState?> FindByMatchIdAsync(string matchId, CancellationToken ct = default);

    /// <summary>
    /// Atomically apply <paramref name="mutator"/> to the current state and persist
    /// the result. Returns the updated state, or null if the room is gone, or
    /// throws <see cref="OptimisticConcurrencyException"/> if the version was bumped
    /// concurrently. Future Redis impl will use this exception for retries.
    /// </summary>
    Task<MatchState?> UpdateAsync(string matchId, Func<MatchState, MatchState> mutator, CancellationToken ct = default);

    Task<bool> SetConnectionAsync(string matchId, string playerId, string? connectionId, CancellationToken ct = default);

    Task<RoomMembership?> FindByConnectionAsync(string connectionId, CancellationToken ct = default);

    Task RemoveAsync(string matchId, CancellationToken ct = default);
}

public sealed class OptimisticConcurrencyException : Exception
{
    public OptimisticConcurrencyException(string matchId)
        : base($"Match {matchId} was updated concurrently.")
    {
    }
}
