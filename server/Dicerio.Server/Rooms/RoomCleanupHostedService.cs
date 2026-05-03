using Dicerio.Engine;

namespace Dicerio.Server.Rooms;

public sealed class RoomCleanupOptions
{
    public TimeSpan IdleTtl { get; set; } = TimeSpan.FromMinutes(45);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class RoomCleanupHostedService : BackgroundService
{
    private readonly InMemoryGameRoomStore _store;
    private readonly RoomCleanupOptions _options;
    private readonly ILogger<RoomCleanupHostedService> _logger;

    public RoomCleanupHostedService(
        InMemoryGameRoomStore store,
        RoomCleanupOptions options,
        ILogger<RoomCleanupHostedService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
                Sweep();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Room cleanup sweep failed");
            }
        }
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - _options.IdleTtl;
        foreach (var (state, lastActivity, _) in _store.SnapshotEntries())
        {
            if (lastActivity > cutoff)
            {
                continue;
            }

            if (state.Phase is MatchPhase.GameOver or MatchPhase.WaitingForOpponent)
            {
                _logger.LogInformation("Reaping idle room {RoomCode} (matchId={MatchId})", state.RoomCode, state.MatchId);
                _store.RemoveAsync(state.MatchId).GetAwaiter().GetResult();
            }
        }
    }
}
