import type { DiceDto, LastEventKind, MatchStateDto } from "./protocol";

export interface HistoryEntry {
  id: number;
  version: number;
  tMs: number;
  kind: LastEventKind;
  player: string | null;
  points: number | null;
  dice: number[] | null;
  text: string;
}

export interface HistoryState {
  entries: HistoryEntry[];
  lastVersion: number;
  prevDice: DiceDto[] | null;
}

export function emptyHistory(): HistoryState {
  return { entries: [], lastVersion: 0, prevDice: null };
}

function nameOf(state: MatchStateDto, playerId: string | null | undefined): string {
  if (!playerId) return "Player";
  return state.players.find((p) => p.playerId === playerId)?.displayName ?? "Player";
}

/**
 * Pure derivation: given the previous history state and a new MatchStateDto,
 * return the appended history (or unchanged if the state is older / duplicate).
 *
 * History is computed from `state.lastEvent` and the diff against `prevDice`,
 * so a fresh client mid-match starts with empty history — that matches the
 * "no need to store" requirement.
 */
export function reduceHistory(prev: HistoryState, state: MatchStateDto): HistoryState {
  if (state.version <= prev.lastVersion) {
    return prev;
  }

  const tMs = state.elapsedMs;
  const event = state.lastEvent;
  const player = event.playerId;
  const playerName = nameOf(state, player);
  const prevDice = prev.prevDice;

  let dice: number[] | null = null;
  let text = "";

  switch (event.kind) {
    case "PlayerJoined":
      text = `${playerName} joined`;
      break;
    case "MatchStarted":
      text = `Match started — ${playerName} goes first`;
      break;
    case "Rolled": {
      // Newly rolled values are the unlocked, non-zero dice in the new state.
      dice = state.dice.filter((d) => !d.locked && d.value !== 0).map((d) => d.value);
      text = `${playerName} rolled  ${formatDice(dice)}`;
      break;
    }
    case "Locked": {
      // Just-locked indices are those locked now but not in prev.
      if (prevDice && prevDice.length === state.dice.length) {
        dice = [];
        for (let i = 0; i < state.dice.length; i++) {
          const wasLocked = prevDice[i]?.locked ?? false;
          if (state.dice[i]!.locked && !wasLocked) {
            dice.push(state.dice[i]!.value);
          }
        }
      }
      text = `${playerName} locked  ${formatDice(dice ?? [])}  +${event.points ?? 0} (turn ${state.turnScore})`;
      break;
    }
    case "HotDice": {
      // All six were just locked; values are gone from new state, so pull from prev.
      if (prevDice) {
        dice = [];
        for (let i = 0; i < state.dice.length; i++) {
          const wasLocked = prevDice[i]?.locked ?? false;
          if (!wasLocked && prevDice[i]) {
            dice.push(prevDice[i]!.value);
          }
        }
      }
      text = `${playerName} hot dice  ${formatDice(dice ?? [])}  +${event.points ?? 0} — reroll all six`;
      break;
    }
    case "Banked":
      text = `${playerName} banked +${event.points ?? 0}`;
      break;
    case "Busted":
      // Bust faces are visible on state.dice (unlocked or locked from earlier in turn).
      dice = state.dice.filter((d) => !d.locked && d.value !== 0).map((d) => d.value);
      text = `${playerName} busted on  ${formatDice(dice)} — turn lost`;
      break;
    case "Forfeit":
      text = event.message ? `${playerName}: ${event.message}` : `${playerName} forfeited`;
      break;
    case "GameOver":
      text = event.message ?? `${playerName} wins`;
      break;
    case "None":
    default:
      // No-op transitions (e.g. deferred BustReveal ack carries LastEventKind.None);
      // advance bookkeeping but don't append an entry.
      return { ...prev, lastVersion: state.version, prevDice: state.dice };
  }

  if (!text) {
    return { ...prev, lastVersion: state.version, prevDice: state.dice };
  }

  const entry: HistoryEntry = {
    id: prev.entries.length + 1,
    version: state.version,
    tMs,
    kind: event.kind,
    player,
    points: event.points,
    dice,
    text,
  };

  return {
    entries: [...prev.entries, entry],
    lastVersion: state.version,
    prevDice: state.dice,
  };
}

function formatDice(values: number[]): string {
  if (values.length === 0) return "—";
  return values.join(" · ");
}

export function formatRelativeTime(tMs: number): string {
  if (tMs < 1000) return "0s";
  const total = Math.floor(tMs / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return m > 0 ? `${m}m${s.toString().padStart(2, "0")}s` : `${s}s`;
}
