# Dicerio

A 1v1 online dice game in the browser, Farkle / Kingdom Come Deliverance 2 style — push your luck, lock scoring dice, bank or bust. Server-authoritative rules and RNG over SignalR.

```
.
├── server/                    # ASP.NET Core 9 solution
│   ├── Dicerio.Engine/        # Pure rules engine (no SignalR refs)
│   ├── Dicerio.Engine.Tests/  # xUnit tests for the engine
│   └── Dicerio.Server/        # Web host + SignalR GameHub
└── client/                    # Vite + React 19 + TS + @microsoft/signalr
```

## Tech

- **Server:** ASP.NET Core 9 (`net9.0`), SignalR hub, in-memory store with TTL sweeper, CORS for the SPA origin.
- **Client:** TypeScript, React 19, Vite, `@microsoft/signalr` 8.x.
- **RNG:** `System.Security.Cryptography.RandomNumberGenerator` for both dice rolls and first-player selection.
- **Tests:** xUnit, table-driven scoring + state-machine cases.

## Run it

### Prereqs

- .NET SDK 9.x
- Node 20+ (Node 25 / npm 11 works; Node 22+ recommended)

### Server

```bash
cd server
dotnet test                                 # 67 unit tests
dotnet run --project Dicerio.Server         # http://localhost:5075
```

The hub is at `POST /hubs/game` (SignalR negotiate). Health probe at `GET /health`.

### Client

```bash
cd client
npm install
npm run dev                                 # http://localhost:5173
```

By default the client points at `http://localhost:5075`. Override with `VITE_SERVER_URL` in `.env` or shell:

```bash
VITE_SERVER_URL=http://192.168.1.20:5075 npm run dev
```

### CORS / SignalR notes

`Program.cs` reads `Cors:Origins` from `appsettings.json` (defaults to `http://localhost:5173` and `http://127.0.0.1:5173`) and registers a CORS policy that includes `AllowCredentials()` — required for SignalR's `negotiate` + WebSocket upgrade. To deploy the SPA elsewhere, append the production origin to that array.

If you put a reverse proxy in front of the server, make sure it doesn't strip `Connection: Upgrade` and that sticky sessions are on (single-process is the only supported topology in v1; see *Limits* below).

## Game rules (v1)

- Six d6 per roll, 2 players, first to **TargetScore** wins (default 3 000, configurable per room from 100 to 100 000).
- **Turn flow:** start with all six dice unlocked → server rolls all unlocked dice → if no scoring subset exists you **bust** (lose this turn's accumulated score; turn passes); otherwise you must **lock** a non-empty scoring subset of the just-rolled dice. Then either **roll again** (rolls only the still-unlocked dice) or **bank** (add turn score to match score, pass turn).
- **Bust reveal:** on a bust the match enters a transient `BustReveal` phase. Both clients see the actual busted faces with a shake animation, neither side can act, and the server auto-advances to the opponent's turn after `BustRevealOptions.Duration` (default 1.8 s). This guarantees the loser gets a clean read on what killed them and the opponent can't paper over it by rolling instantly.
- **Hot dice:** if a lock results in all six dice being locked you re-roll all six and continue the same turn (turn score is preserved).
- **Lock validity:** every locked die must participate in a valid scoring combination. Orphan dice are rejected by the server (`InvalidLockPartition`).
- **First player:** chosen uniformly at random server-side when the second player joins.

### Scoring table

| Combination | Points |
|---|---|
| Single 1 | 100 |
| Single 5 | 50 |
| Three 1s | 1 000 |
| Three Ns (N≠1) | N × 100 |
| Four of a kind | 2 × three-of-a-kind value |
| Five of a kind | 4 × three-of-a-kind value |
| Six of a kind | 8 × three-of-a-kind value |
| Full straight 1-2-3-4-5-6 | 1 500 |

Notes:

- Singles only score for **1** and **5**. There are no single 2/3/4/6 scores.
- The "double per extra die" extension was chosen over a fixed 4/5/6-of-a-kind table because it scales 1s correctly and is easy to remember.
- Only the full straight (1-2-3-4-5-6) scores in v1. Partial straights (1-5, 2-6) and three-pairs / two-triplets variants are deliberately excluded — they can be added behind the same `RuleSet.Id` without protocol changes.

## Architecture

### Server authority & security

- The server owns dice faces, turn ownership, all scoring, all phase transitions, and the win check. The client sends only intents (`CreateRoom`, `JoinRoom`, `RollAgain`, `SubmitLock`, `PreviewLock`, `Bank`, `LeaveRoom`).
- `MatchStateDto` is what the client renders; it is recomputed from the canonical engine state on every transition. The server **never** trusts a client-provided score, dice value, phase, or active player.
- Rolls use `RandomNumberGenerator.GetInt32(1, 7)`. First-seat selection uses `RandomNumberGenerator.GetInt32(0, 2)`.

### Engine

`Dicerio.Engine` is a pure class library with **no** ASP.NET / SignalR / DI references. The hub project is the only thing that knows about transports.

- `Scoring` — partition-based scorer + bust detector, table-tested.
- `FarkleEngine` — pure functions `(state, intent) → EngineResult`. Returns either the next `MatchState` or an `EngineError` with a stable `EngineErrorCode`.
- `IDiceRoller` — abstraction so tests can script faces.

### Hub

`GameHub` is thin: it owns connection ↔ player mapping, calls `FarkleEngine`, and broadcasts `StateUpdated` to the `match:{matchId}` group on every legal transition. Illegal moves return a per-caller `GameError` event with a stable code; canonical state is unchanged.

Server events:

- `StateUpdated(MatchStateDto)` — the only thing the UI needs to render.
- `GameError(HubErrorPayload)` — illegal action feedback (caller-only).
- `MatchEnded(winnerId)` — convenience event broadcast at game over.

### Room store

`IGameRoomStore` is the persistence boundary; the in-memory implementation is the MVP. The interface is shaped so a Redis-backed implementation can swap in later (`UpdateAsync` is the natural place for optimistic concurrency / `WATCH/MULTI/EXEC`).

A `RoomCleanupHostedService` sweeps every 2 minutes and reaps idle rooms in `WaitingForOpponent` or `GameOver` after 45 minutes (configurable via `RoomCleanupOptions`).

### Room codes

4-character codes drawn from a Crockford-ish unambiguous alphabet (`23456789ABCDEFGHJKLMNPQRSTVWXYZ` — no `0/O/1/I/U`). Generated via `RandomNumberGenerator` and regenerated on the rare collision (up to 9 attempts). Length is `RoomCode.Length`; bump it if you ever expect more than a few thousand concurrent rooms (collision probability with 4 chars ≈ active-rooms / 810,000).

`RoomCode.Normalize` strips spaces/dashes, uppercases, and rejects ambiguous characters so that pasting `"abc-d3-2g"` is forgiving but `"O01"` is rejected.

### Rate limiting

`JoinRateLimiter` (per connection id) caps `JoinRoom` at 12 attempts/minute by default. Throws a `HubException` once exhausted. This is single-process, in-memory; if you scale beyond one host put a real shared limiter (Redis token bucket, IP-based) in front.

### Disconnect policy

**Forfeit on disconnect, no grace period** if the disconnect happens during `AwaitingRoll` or `AwaitingLock`. The opponent immediately wins via `LastEventKind.Forfeit`. If the disconnect happens in `WaitingForOpponent` (host alone) or `GameOver` (match already done), nothing happens beyond marking the player offline; the room is reaped by the cleanup sweeper.

This is intentionally aggressive for the MVP — a "30s grace window with a reconnect token" upgrade fits neatly into `OnDisconnectedAsync` without protocol changes.

## Manual test checklist

Open two browser windows (or one Chrome + one Firefox/incognito) at `http://localhost:5173`.

1. **Host:** type a name → `Create room`. Confirm the 4-char room code is shown big, copy button works, `?code=…` URL hint is visible. State should show `WaitingForOpponent`.
2. **Guest:** paste the code → `Join`. Both windows should now show the dice board, identical scores, and the same active-player highlight.
3. **Active player rolls.** Confirm only their unlocked dice get values; the inactive player sees the same dice but their action buttons are disabled.
4. **Lock a non-scoring die** (try selecting a `2` and a `3` and pressing Lock). Confirm the inactive client's state doesn't change; the active client sees a `GameError` with code `InvalidLockPartition`.
5. **Lock a scoring die** (e.g. a single `1`). Confirm turn score increments by the right amount, dice grid shows the locked die in green, phase is `AwaitingRoll`.
6. **Roll again.** Confirm only the unlocked dice get re-rolled; the locked die keeps its face and `locked` flag.
7. **Force a bust:** keep rolling until you see "X busted — turn lost". Confirm the active player loses **only** turn score (match score unchanged), turn passes, dice show the busted faces until the new active player rolls.
8. **Hot dice:** lock all six dice in one roll (best chance: roll a straight and lock all six). Confirm all six dice clear and you can roll again with the turn score preserved.
9. **Bank.** Confirm match score increments by the turn score, turn passes, dice clear, turn score resets to 0.
10. **Win.** Use a small target score (e.g. 1 000 in lobby options) and play to a banked total ≥ 1 000. Confirm `phase=GameOver`, `winnerId` set, both clients show the same winner card.
11. **Disconnect / forfeit.** Mid-match, close the active player's browser tab. Confirm the other window briefly shows them as offline then transitions to `GameOver` with `LastEventKind.Forfeit` and the surviving player as the winner.
12. **Bad room codes.** From the join form, try `XXXXXX`, `00`, and a stale code from a previous session. Confirm errors `RoomNotFound` / `InvalidCode`.
13. **Duplicate join.** With a 2-player match in progress, try joining the same room from a third tab. Confirm `GameAlreadyStarted` / `RoomFull`.
14. **Rate limiting (optional).** Spam Join with bogus codes from one tab; after 12 in a minute the server returns `RateLimited`.

A scripted version of (1)–(10) lives in `client/src/smoke.ts` (`npx tsx src/smoke.ts` while the server is running) and plays a full deterministic-ish match to `GameOver`.

## Limits / known gaps

- **Single-process only.** State is in `InMemoryGameRoomStore`; restart drops every active match. Swap to Redis via the `IGameRoomStore` interface to scale out. SignalR also needs a backplane in that case.
- **No reconnect token.** Tabs that drop are treated as forfeits. The protocol leaves room for an opaque resume token without server changes.
- **No spectators / no rematch button.** Easy to add: a `Rematch` hub method that resets `MatchState` while keeping the same `Players` and `RoomCode`.
- **Cosmetic v1.** No animations, no sounds, no avatars, no badges/jokers/weighted dice. The protocol exposes `RuleSet.Id` so future variants can light up new combinations without breaking older clients.
- **No persistent leaderboards.** Out of scope for v1.

## Deploy to Render.com (free)

The repo ships with a `Dockerfile` (multi-stage: builds the SPA with Vite, builds the .NET server, ships a slim runtime image with the SPA bundled into `wwwroot/`) and a Render Blueprint (`render.yaml`). One process, one origin, one URL — no CORS configuration needed.

1. **Push the repo to GitHub.** From this directory:
   ```bash
   git init && git add . && git commit -m "initial"
   git branch -M main
   git remote add origin git@github.com:YOUR_USER/dicerio.git
   git push -u origin main
   ```
2. **Sign in to [render.com](https://render.com)** with the GitHub button (no credit card needed for the free tier).
3. From the dashboard click **New → Blueprint**, point it at your repo. Render reads `render.yaml`, creates a single web service (Docker, free plan, Frankfurt region), and starts the first build.
4. First build takes ~3 minutes. After that you get `https://dicerio-<hash>.onrender.com`. That URL serves both the SPA and the `/hubs/game` SignalR endpoint.
5. **Custom region or name:** edit `render.yaml` (`region: oregon|ohio|singapore|frankfurt|...`, `name: …`) and push.

### Free-tier caveats

- **Cold starts.** The free instance spins down after 15 minutes with no HTTP/WebSocket traffic. The next request takes ~30–60 s to wake the box. Once awake, both players play normally. SignalR's `withAutomaticReconnect()` (already on) handles the reconnect.
- **Single instance only.** State is in `InMemoryGameRoomStore`; the free plan is single-instance which is what we want anyway.
- **No persistence across redeploys.** A new push or a Render restart drops every active match. Acceptable for hobby use.

### Verify locally before pushing

```bash
docker build -t dicerio:local .
docker run --rm -p 8080:8080 dicerio:local
# in another shell:
curl http://localhost:8080/health
open http://localhost:8080
```

The bundled SPA is the `dist/` produced by `npm run build` against `client/`, so a stale `client/dist` from local dev is irrelevant — the Docker build creates a fresh one.

## Wire protocol cheatsheet

Hub URL: `http://<server>/hubs/game`

| Direction | Name | Payload |
|---|---|---|
| C→S | `CreateRoom` | `{ displayName?, targetScore? }` → `{ roomCode, matchId, playerId }` |
| C→S | `JoinRoom` | `{ roomCode, displayName? }` → `{ roomCode, matchId, playerId }` |
| C→S | `RollAgain` | _no args_ |
| C→S | `SubmitLock` | `int[]` (indexes into `state.dice`) |
| C→S | `Bank` | _no args_ |
| C→S | `LeaveRoom` | _no args_ |
| S→C | `StateUpdated` | `MatchStateDto` (broadcast on every transition) |
| S→C | `GameError` | `{ code, message }` (caller-only on illegal moves) |
| S→C | `MatchEnded` | `winnerId` (broadcast on game over) |

Stable error codes: `NotYourTurn`, `WrongPhase`, `EmptyLock`, `InvalidLockIndex`, `InvalidLockPartition`, `MatchNotStarted`, `MatchAlreadyOver`, `UnknownPlayer`, plus the join-only `RoomNotFound`, `RoomFull`, `GameAlreadyStarted`, `InvalidCode`, `RateLimited`.
