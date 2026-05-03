// Headless smoke test that drives the GameHub end-to-end with two clients
// and plays a deterministic-ish strategy until the match ends. Run with:
//   npx tsx src/smoke.ts
// (requires the server running on http://localhost:5075).

import {
  HubConnectionBuilder,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import type { HubErrorPayload, MatchStateDto } from "../src/protocol";

const HUB_URL = process.env.HUB_URL ?? "http://localhost:5075/hubs/game";
const TARGET = parseInt(process.env.TARGET ?? "1500", 10);

function makeConn(label: string) {
  const onState: ((s: MatchStateDto) => void)[] = [];
  const conn: HubConnection = new HubConnectionBuilder()
    .withUrl(HUB_URL)
    .configureLogging(LogLevel.Warning)
    .build();
  conn.on("StateUpdated", (s: MatchStateDto) => {
    onState.forEach((fn) => fn(s));
  });
  conn.on("GameError", (e: HubErrorPayload) => {
    console.log(`[${label}] error ${e.code}: ${e.message}`);
  });
  return { conn, on: (fn: (s: MatchStateDto) => void) => onState.push(fn) };
}

function pickAnyScoringDie(state: MatchStateDto): number[] {
  const scoringSingles: number[] = [];
  const counts: Record<number, number[]> = {};
  state.dice.forEach((d, idx) => {
    if (d.locked || d.value === 0) return;
    if (d.value === 1 || d.value === 5) scoringSingles.push(idx);
    counts[d.value] ??= [];
    counts[d.value].push(idx);
  });
  for (const [face, idxs] of Object.entries(counts)) {
    if (idxs.length >= 3 && Number(face) !== 1 && Number(face) !== 5) {
      return idxs.slice(0, 3);
    }
  }
  if (scoringSingles.length > 0) {
    return [scoringSingles[0]!];
  }
  for (const [, idxs] of Object.entries(counts)) {
    if (idxs.length >= 3) {
      return idxs.slice(0, 3);
    }
  }
  return [];
}

async function main() {
  const host = makeConn("host");
  const guest = makeConn("guest");

  let latest: MatchStateDto | null = null;
  let waitingResolver: ((s: MatchStateDto) => void) | null = null;
  const onAny = (s: MatchStateDto) => {
    latest = s;
    if (waitingResolver) {
      const r = waitingResolver;
      waitingResolver = null;
      r(s);
    }
  };
  host.on(onAny);
  guest.on(onAny);

  await host.conn.start();
  await guest.conn.start();

  const create = await host.conn.invoke<{ roomCode: string; matchId: string; playerId: string }>(
    "CreateRoom",
    { displayName: "Host", targetScore: TARGET, maxPlayers: null }
  );
  const join = await guest.conn.invoke<{ roomCode: string; matchId: string; playerId: string }>(
    "JoinRoom",
    { roomCode: create.roomCode, displayName: "Guest" }
  );
  console.log("room", create.roomCode, "host", create.playerId, "guest", join.playerId);

  function nextStateAfter(version: number): Promise<MatchStateDto> {
    if (latest && latest.version > version) return Promise.resolve(latest);
    return new Promise((resolve) => {
      waitingResolver = (s) => {
        if (s.version > version) resolve(s);
        else waitingResolver = waitingResolver;
      };
    });
  }

  let turns = 0;
  const start = Date.now();
  while ((latest as MatchStateDto | null) === null || (latest as MatchStateDto).phase !== "GameOver") {
    if (Date.now() - start > 60_000) {
      throw new Error("timed out without GameOver");
    }
    if (!latest) {
      await new Promise((r) => setTimeout(r, 50));
      continue;
    }
    const s: MatchStateDto = latest;
    if (s.phase === "WaitingForOpponent") {
      await new Promise((r) => setTimeout(r, 50));
      continue;
    }
    if (s.phase === "BustReveal") {
      // Server schedules an automatic AcknowledgeBust after a short delay; just wait.
      await nextStateAfter(s.version);
      continue;
    }
    const myConn = s.activePlayerId === create.playerId ? host.conn : guest.conn;
    const before = s.version;
    if (s.phase === "AwaitingRoll") {
      await myConn.invoke("RollAgain");
      await nextStateAfter(before);
    } else if (s.phase === "AwaitingLock") {
      const idx = pickAnyScoringDie(s);
      if (idx.length === 0) {
        throw new Error("AwaitingLock with no scoring die — server bug");
      }
      await myConn.invoke("SubmitLock", idx);
      const after = await nextStateAfter(before);
      if (after.phase === "AwaitingRoll" && after.turnScore > 0) {
        await myConn.invoke("Bank");
        await nextStateAfter(after.version);
      }
    }
    turns++;
    if (turns > 400) throw new Error("turn limit exceeded");
  }

  const final = latest as unknown as MatchStateDto;
  console.log("GAME OVER", { winner: final.winnerId, scores: final.players.map((p) => `${p.displayName}=${p.matchScore}`) });

  await host.conn.stop();
  await guest.conn.stop();
}

main().catch((err) => {
  console.error("smoke failed", err);
  process.exit(1);
});
