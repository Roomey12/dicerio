using Dicerio.Engine;
using Dicerio.Server.Hubs;
using Dicerio.Server.Rooms;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicyName = "DicerioSpa";

var spaOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://127.0.0.1:5173" };

builder.Services.AddCors(opts =>
{
    opts.AddPolicy(CorsPolicyName, policy =>
    {
        policy.WithOrigins(spaOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddSingleton<InMemoryGameRoomStore>();
builder.Services.AddSingleton<IGameRoomStore>(sp => sp.GetRequiredService<InMemoryGameRoomStore>());
builder.Services.AddSingleton<IDiceRoller, CryptoDiceRoller>();

builder.Services.AddSingleton(new RoomCleanupOptions
{
    IdleTtl = TimeSpan.FromMinutes(45),
    PollInterval = TimeSpan.FromMinutes(2),
});
builder.Services.AddHostedService<RoomCleanupHostedService>();

builder.Services.AddSingleton(new JoinRateLimiterOptions
{
    MaxAttemptsPerWindow = 12,
    Window = TimeSpan.FromMinutes(1),
});
builder.Services.AddSingleton<JoinRateLimiter>();

builder.Services.AddSingleton(new BustRevealOptions
{
    Duration = TimeSpan.FromMilliseconds(1800),
});

var app = builder.Build();

app.UseCors(CorsPolicyName);

// Serve the bundled SPA from wwwroot (populated at container build time by the
// Dockerfile). When wwwroot is empty (e.g. local `dotnet run`), these are
// no-ops and the SPA is served by Vite on a different origin instead.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.MapHub<GameHub>("/hubs/game");

// SPA fallback: any non-/hubs/* GET that doesn't match a static file falls
// back to index.html so client-side routes (and ?code=… deep links) work.
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program
{
}
