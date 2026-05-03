import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { HubConnectionState } from "@microsoft/signalr";
import "./App.css";
import { GameClient } from "./gameClient";
import type {
  HubErrorPayload,
  LastEventKind,
  MatchPhase,
  MatchStateDto,
} from "./protocol";
import { Die } from "./components/Die";
import {
  emptyHistory,
  formatRelativeTime,
  reduceHistory,
  type HistoryEntry,
  type HistoryState,
} from "./history";
import { isSoundEnabled, play, primeAudio, setSoundEnabled } from "./sounds";

type Screen = "lobby" | "match";

function dispatchSounds(state: MatchStateDto, wasMyTurn: boolean): void {
  const isMyTurn = !!state.youAre && state.activePlayerId === state.youAre && state.phase !== "GameOver";

  switch (state.lastEvent.kind) {
    case "Rolled":
      play("roll");
      break;
    case "Locked":
      play("lock");
      break;
    case "Banked":
      play("bank");
      break;
    case "Busted":
      play("bust");
      break;
    case "HotDice":
      play("hotdice");
      break;
    case "GameOver":
      if (state.winnerId === state.youAre) play("win");
      break;
    case "Forfeit":
      if (state.winnerId === state.youAre) play("win");
      else play("bust");
      break;
    case "MatchStarted":
      if (isMyTurn) play("yourTurn");
      break;
    default:
      break;
  }

  // Detect "your turn just started" via opponent action — fires after the
  // primary event sound (e.g. their bank), so the player gets a clear cue.
  if (!wasMyTurn && isMyTurn && state.lastEvent.kind !== "MatchStarted") {
    setTimeout(() => play("yourTurn"), 220);
  }
}

function RulesTip() {
  const [open, setOpen] = useState(false);
  return (
    <div className="rules-tip">
      <button
        className="rules-tip-button"
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-controls="rules-tip-popover"
        title="Rules"
      >
        ? rules
      </button>
      {open ? (
        <>
          <div className="rules-tip-backdrop" onClick={() => setOpen(false)} />
          <div id="rules-tip-popover" role="dialog" className="rules-tip-popover">
            <div className="rules-tip-head">
              <strong>How to play</strong>
              <button className="ghost" type="button" onClick={() => setOpen(false)}>
                close
              </button>
            </div>
            <RulesBody />
          </div>
        </>
      ) : null}
    </div>
  );
}

function RulesBody() {
  return (
    <div className="rules-body">
      <p>
        Two players, six dice. On your turn you <em>roll</em>, must <em>lock</em> at least one
        scoring die from that roll, then either <em>roll again</em> with the still-unlocked dice
        or <em>bank</em> your turn score and pass.
      </p>
      <ul>
        <li>If a roll has <strong>no</strong> scoring dice, you <strong>bust</strong> and lose this turn's points (your match score is unchanged).</li>
        <li>Every locked die must belong to a scoring combination — you can't lock orphan dice.</li>
        <li>Lock all six dice in a single turn and you get <strong>hot dice</strong> — reroll all six and keep going.</li>
        <li>First player is chosen at random when the second player joins.</li>
        <li>First to the target score (3,000 by default, host-configurable) wins.</li>
      </ul>
      <h4>Scoring</h4>
      <table className="rules-table">
        <tbody>
          <tr><td>Single 1</td><td>100</td></tr>
          <tr><td>Single 5</td><td>50</td></tr>
          <tr><td>Three 1s</td><td>1,000</td></tr>
          <tr><td>Three Ns (N≠1)</td><td>N × 100</td></tr>
          <tr><td>Four of a kind</td><td>2 × three-of-a-kind value</td></tr>
          <tr><td>Five of a kind</td><td>4 × three-of-a-kind value</td></tr>
          <tr><td>Six of a kind</td><td>8 × three-of-a-kind value</td></tr>
          <tr><td>Straight 1-2-3-4-5-6</td><td>1,500</td></tr>
        </tbody>
      </table>
      <p className="rules-foot">
        Singles only score for <strong>1</strong> and <strong>5</strong>. Two/three/four/six on
        their own are worthless — you need three or more of them to count.
      </p>
    </div>
  );
}

function deepLinkCode(): string | null {
  const params = new URLSearchParams(window.location.search);
  const raw = params.get("code");
  if (!raw) return null;
  return raw.trim().toUpperCase();
}

function eventLabel(kind: LastEventKind, players: { playerId: string; displayName: string }[], event: MatchStateDto["lastEvent"]): string {
  const who = event.playerId
    ? players.find((p) => p.playerId === event.playerId)?.displayName ?? "Player"
    : "";
  switch (kind) {
    case "PlayerJoined":
      return `${who} joined`;
    case "MatchStarted":
      return `${who} starts`;
    case "Rolled":
      return `${who} rolled`;
    case "Locked":
      return `${who} locked +${event.points ?? 0}`;
    case "Banked":
      return `${who} banked ${event.points ?? 0}`;
    case "Busted":
      return `${who} busted — turn lost`;
    case "HotDice":
      return `${who} got hot dice — reroll all six`;
    case "GameOver":
      return event.message ?? `${who} wins`;
    case "Forfeit":
      return event.message ?? `${who} forfeited`;
    default:
      return "";
  }
}

export default function App() {
  const [connectionState, setConnectionState] = useState<HubConnectionState>(HubConnectionState.Disconnected);
  const [screen, setScreen] = useState<Screen>("lobby");
  const [match, setMatch] = useState<MatchStateDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [selectedIndexes, setSelectedIndexes] = useState<number[]>([]);
  const [copyFeedback, setCopyFeedback] = useState<string>("");
  const [history, setHistory] = useState<HistoryState>(emptyHistory());
  const [soundOn, setSoundOn] = useState<boolean>(() => isSoundEnabled());
  const currentMatchIdRef = useRef<string | null>(null);
  const lastVersionRef = useRef<number>(0);
  const previouslyMyTurnRef = useRef<boolean>(false);
  const clientRef = useRef<GameClient | null>(null);

  useEffect(() => {
    const c = new GameClient({
      onState: (s) => {
        const isNewMatch = currentMatchIdRef.current !== s.matchId;
        if (isNewMatch) {
          currentMatchIdRef.current = s.matchId;
          setHistory(reduceHistory(emptyHistory(), s));
          lastVersionRef.current = 0;
          previouslyMyTurnRef.current = false;
        } else {
          setHistory((h) => reduceHistory(h, s));
        }

        if (s.version > lastVersionRef.current) {
          dispatchSounds(s, previouslyMyTurnRef.current);
          lastVersionRef.current = s.version;
          previouslyMyTurnRef.current =
            !!s.youAre && s.activePlayerId === s.youAre && s.phase !== "GameOver";
        }

        setMatch(s);
        setScreen("match");
        setSelectedIndexes([]);
      },
      onError: (e: HubErrorPayload) => {
        setError(`${e.code}: ${e.message}`);
      },
      onMatchEnded: () => {
        // state will already carry GameOver; nothing to do here.
      },
      onConnectionChange: (s) => setConnectionState(s),
    });
    clientRef.current = c;
    return () => {
      void c.stop();
      clientRef.current = null;
    };
  }, []);

  const youAre = match?.youAre ?? null;
  const isYourTurn = !!match && !!youAre && match.activePlayerId === youAre;

  const youHaveScoringDie = useMemo(() => {
    if (!match) return false;
    return match.dice.some((d) => !d.locked && d.value !== 0);
  }, [match]);

  const handleCreate = useCallback(
    async (displayName: string, targetScore: number | undefined) => {
      if (!clientRef.current) return;
      setError(null);
      setBusy(true);
      try {
        primeAudio();
        await clientRef.current.createRoom(displayName || undefined, targetScore);
        play("createRoom");
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err));
      } finally {
        setBusy(false);
      }
    },
    []
  );

  const handleJoin = useCallback(
    async (roomCode: string, displayName: string) => {
      if (!clientRef.current) return;
      setError(null);
      setBusy(true);
      try {
        await clientRef.current.joinRoom(roomCode, displayName || undefined);
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err));
      } finally {
        setBusy(false);
      }
    },
    []
  );

  const handleLeave = useCallback(async () => {
    if (!clientRef.current) return;
    try {
      await clientRef.current.leave();
    } finally {
      setMatch(null);
      setScreen("lobby");
      setSelectedIndexes([]);
      setHistory(emptyHistory());
      currentMatchIdRef.current = null;
    }
  }, []);

  const handleRoll = useCallback(async () => {
    if (!clientRef.current) return;
    setError(null);
    try {
      await clientRef.current.roll();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, []);

  const handleSubmitLock = useCallback(async () => {
    if (!clientRef.current) return;
    setError(null);
    try {
      await clientRef.current.submitLock(selectedIndexes);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [selectedIndexes]);

  const handleBank = useCallback(async () => {
    if (!clientRef.current) return;
    setError(null);
    try {
      await clientRef.current.bank();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, []);

  const toggleDie = useCallback(
    (idx: number) => {
      if (!match) return;
      const die = match.dice[idx];
      if (!die || die.locked || die.value === 0) return;
      if (match.phase !== "AwaitingLock") return;
      if (!isYourTurn) return;
      const wasSelected = selectedIndexes.includes(idx);
      setSelectedIndexes((prev) =>
        wasSelected ? prev.filter((i) => i !== idx) : [...prev, idx]
      );
      play(wasSelected ? "diceDeselect" : "diceSelect");
    },
    [match, isYourTurn, selectedIndexes]
  );

  const copyRoomCode = useCallback(async () => {
    if (!match) return;
    try {
      await navigator.clipboard.writeText(match.roomCode);
      setCopyFeedback("copied");
      setTimeout(() => setCopyFeedback(""), 1500);
    } catch {
      setCopyFeedback("press ctrl-c");
    }
  }, [match]);

  return (
    <div className="app">
      <header className="brand" onPointerDownCapture={primeAudio}>
        <h1>Dicerio</h1>
        <small>1v1 push-your-luck dice</small>
        {match && match.phase !== "WaitingForOpponent" ? (
          <span className="target-pill" title="First to this score wins">
            first to {match.rules.targetScore.toLocaleString()}
          </span>
        ) : null}
        <span className="connection-pill" data-state={HubConnectionState[connectionState]}>
          {HubConnectionState[connectionState]}
        </span>
        <button
          className="icon-button"
          type="button"
          title={soundOn ? "Mute sounds" : "Unmute sounds"}
          aria-pressed={soundOn}
          onClick={() => {
            const next = !soundOn;
            setSoundEnabled(next);
            setSoundOn(next);
            if (next) {
              primeAudio();
              play("click");
            }
          }}
        >
          {soundOn ? "♪ on" : "♪ off"}
        </button>
        <RulesTip />
      </header>

      {screen === "lobby" || !match ? (
        <Lobby onCreate={handleCreate} onJoin={handleJoin} busy={busy} error={error} />
      ) : match.phase === "WaitingForOpponent" ? (
        <Waiting code={match.roomCode} onCopy={copyRoomCode} copyFeedback={copyFeedback} onLeave={handleLeave} />
      ) : (
        <Board
          state={match}
          isYourTurn={isYourTurn}
          selectedIndexes={selectedIndexes}
          toggleDie={toggleDie}
          onRoll={handleRoll}
          onSubmitLock={handleSubmitLock}
          onBank={handleBank}
          onLeave={handleLeave}
          error={error}
          youHaveScoringDie={youHaveScoringDie}
          history={history.entries}
        />
      )}
    </div>
  );
}

interface LobbyProps {
  onCreate: (displayName: string, targetScore: number | undefined) => Promise<void>;
  onJoin: (roomCode: string, displayName: string) => Promise<void>;
  busy: boolean;
  error: string | null;
}

const TARGET_MIN = 100;
const TARGET_MAX = 100_000;

function Lobby({ onCreate, onJoin, busy, error }: LobbyProps) {
  const [displayName, setDisplayName] = useState<string>("");
  const [roomCode, setRoomCode] = useState<string>("");
  const [targetScore, setTargetScore] = useState<string>("3000");

  useEffect(() => {
    const code = deepLinkCode();
    if (code) setRoomCode(code);
  }, []);

  const parsedTarget = parseInt(targetScore, 10);
  const targetValid =
    Number.isFinite(parsedTarget) && parsedTarget >= TARGET_MIN && parsedTarget <= TARGET_MAX;

  return (
    <>
      <div className="lobby">
        <h2>Host a match</h2>
        <input
          type="text"
          placeholder="Display name (optional)"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          maxLength={24}
        />
        <div className="options">
          <label htmlFor="target">Target score</label>
          <input
            id="target"
            type="text"
            inputMode="numeric"
            value={targetScore}
            data-invalid={!targetValid}
            onChange={(e) => setTargetScore(e.target.value.replace(/\D/g, "").slice(0, 6))}
          />
        </div>
        {!targetValid ? (
          <div className="hint" data-invalid="true">
            Enter a number between {TARGET_MIN} and {TARGET_MAX.toLocaleString()}.
          </div>
        ) : null}
        <button
          className="primary"
          disabled={busy || !targetValid}
          onClick={() => {
            void onCreate(displayName.trim(), parsedTarget);
          }}
        >
          Create room
        </button>
      </div>

      <div className="lobby">
        <h2>Join a match</h2>
        <div className="row">
          <input
            type="text"
            placeholder="CODE"
            value={roomCode}
            onChange={(e) => setRoomCode(e.target.value.toUpperCase())}
            maxLength={6}
          />
        </div>
        <input
          type="text"
          placeholder="Display name (optional)"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          maxLength={24}
        />
        <button
          className="primary"
          disabled={busy || roomCode.replace(/[^A-Z0-9]/g, "").length < 4}
          onClick={() => void onJoin(roomCode.replace(/[^A-Z0-9]/g, ""), displayName.trim())}
        >
          Join
        </button>
        {error ? <div className="err">{error}</div> : null}
      </div>
    </>
  );
}

interface WaitingProps {
  code: string;
  onCopy: () => void;
  copyFeedback: string;
  onLeave: () => void;
}

function Waiting({ code, onCopy, copyFeedback, onLeave }: WaitingProps) {
  return (
    <div className="lobby waiting">
      <h2>Waiting for opponent</h2>
      <div className="room-code">{code}</div>
      <div className="copy-feedback">{copyFeedback}</div>
      <button className="ghost" onClick={onCopy}>
        Copy room code
      </button>
      <div className="room-code-hint">
        Share the code (or this URL with <code>?code={code}</code>)
      </div>
      <button className="danger" onClick={onLeave}>
        Cancel room
      </button>
    </div>
  );
}

interface BoardProps {
  state: MatchStateDto;
  isYourTurn: boolean;
  selectedIndexes: number[];
  toggleDie: (idx: number) => void;
  onRoll: () => void;
  onSubmitLock: () => void;
  onBank: () => void;
  onLeave: () => void;
  error: string | null;
  youHaveScoringDie: boolean;
  history: HistoryEntry[];
}

function Board({
  state,
  isYourTurn,
  selectedIndexes,
  toggleDie,
  onRoll,
  onSubmitLock,
  onBank,
  onLeave,
  error,
  youHaveScoringDie,
  history,
}: BoardProps) {
  const [me, opponent] = useMemo(() => {
    if (!state.youAre) return [state.players[0], state.players[1]];
    const meIdx = state.players.findIndex((p) => p.playerId === state.youAre);
    if (meIdx < 0) return [state.players[0], state.players[1]];
    return [state.players[meIdx], state.players[(meIdx + 1) % state.players.length]];
  }, [state]);

  const canRoll = isYourTurn && state.phase === "AwaitingRoll" && state.phase !== ("GameOver" as MatchPhase);
  const canLock = isYourTurn && state.phase === "AwaitingLock" && selectedIndexes.length > 0;
  const canBank = isYourTurn && state.phase === "AwaitingRoll" && state.turnScore > 0;
  const isBustReveal = state.phase === "BustReveal";

  const phaseText = (() => {
    if (state.phase === "GameOver") {
      return "Match over";
    }
    if (state.phase === "BustReveal") {
      const who = state.players.find((p) => p.playerId === state.activePlayerId)?.displayName ?? "Player";
      return `${who} busted — turn lost`;
    }
    if (!isYourTurn) {
      return `${opponent?.displayName ?? "Opponent"}'s turn`;
    }
    if (state.phase === "AwaitingRoll") {
      return state.turnScore > 0 ? "Roll again or bank" : "Roll the dice";
    }
    if (state.phase === "AwaitingLock") {
      return "Lock at least one scoring die";
    }
    return "";
  })();

  const eventText = eventLabel(state.lastEvent.kind, state.players, state.lastEvent);

  return (
    <div className="board">
      <div className="scoreboard">
        {[me, opponent].map((p, i) => {
          if (!p) return <div key={i} className="player-card" />;
          const isActive = state.activePlayerId === p.playerId;
          const isYou = state.youAre === p.playerId;
          const pct = Math.min(100, Math.round((p.matchScore / state.rules.targetScore) * 100));
          return (
            <div
              key={p.playerId}
              className="player-card"
              data-active={isActive}
              data-you={isYou}
              data-connected={p.connected}
            >
              <div className="name">{p.displayName}</div>
              <div className="score">
                {p.matchScore.toLocaleString()}
                <span className="score-target">/ {state.rules.targetScore.toLocaleString()}</span>
              </div>
              <div className="score-bar" aria-hidden>
                <div className="score-bar-fill" style={{ width: `${pct}%` }} />
              </div>
              <div className="turn">
                {isActive
                  ? `turn: +${state.turnScore.toLocaleString()}${
                      state.pendingLockHintTotal != null
                        ? ` · best lock: ${state.pendingLockHintTotal}`
                        : ""
                    }`
                  : "waiting"}
              </div>
            </div>
          );
        })}
      </div>

      <div className="dice-area" data-bust={isBustReveal}>
        <div className="phase-banner">
          <span>{phaseText}</span>
          <span className="turn-score">turn: +{state.turnScore.toLocaleString()}</span>
        </div>

        <div className="dice-grid" data-bust={isBustReveal}>
          {state.dice.map((d, idx) => (
            <Die
              key={idx}
              value={d.value}
              locked={d.locked}
              empty={d.value === 0}
              selected={selectedIndexes.includes(idx)}
              disabled={!isYourTurn || state.phase !== "AwaitingLock"}
              onClick={() => toggleDie(idx)}
              bust={isBustReveal && !d.locked}
            />
          ))}
        </div>

        <div className="event-strip" data-kind={state.lastEvent.kind}>
          {eventText}
        </div>

        <div className="actions">
          <button
            className="primary"
            disabled={!canRoll}
            onClick={onRoll}
            title={canRoll ? undefined : "Not in a roll phase"}
          >
            Roll {!youHaveScoringDie && state.phase === "AwaitingRoll" ? "(all 6)" : ""}
          </button>
          <button
            className="primary grow"
            disabled={!canLock}
            onClick={onSubmitLock}
          >
            Lock selected ({selectedIndexes.length})
          </button>
          <button
            className=""
            disabled={!canBank}
            onClick={onBank}
            title={canBank ? "Bank turn score and end your turn" : "Lock something first"}
          >
            Bank +{state.turnScore.toLocaleString()}
          </button>
          <button className="ghost" onClick={onLeave}>
            Leave
          </button>
        </div>

        {error ? <div className="err" style={{ color: "var(--bust)", fontSize: 13 }}>{error}</div> : null}
      </div>

      {state.phase === "GameOver" ? (
        <div className="gameover">
          <h2>
            {state.winnerId
              ? state.winnerId === state.youAre
                ? "You win!"
                : `${state.players.find((p) => p.playerId === state.winnerId)?.displayName ?? "Opponent"} wins`
              : "Match over"}
          </h2>
          <div>
            {state.players
              .map((p) => `${p.displayName}: ${p.matchScore.toLocaleString()}`)
              .join("  ·  ")}
          </div>
          <button className="primary" onClick={onLeave}>
            Back to lobby
          </button>
        </div>
      ) : null}

      <HistoryPanel entries={history} />
    </div>
  );
}

function HistoryPanel({ entries }: { entries: HistoryEntry[] }) {
  return (
    <details className="history">
      <summary>
        History <span className="history-count">{entries.length}</span>
      </summary>
      {entries.length === 0 ? (
        <div className="history-empty">No actions yet.</div>
      ) : (
        <ol className="history-list">
          {[...entries].reverse().map((e) => (
            <li key={e.id} data-kind={e.kind}>
              <span className="history-time">{formatRelativeTime(e.tMs)}</span>
              <span className="history-text">{e.text}</span>
            </li>
          ))}
        </ol>
      )}
    </details>
  );
}
