// Tiny Web-Audio-synth based SFX. No binary assets — every sound is built
// from oscillators / noise + amplitude envelopes. Keeps the bundle small
// and avoids any sample-licensing questions.
//
// AudioContext is created lazily on the first call because browsers (Safari
// in particular) block audio creation until a user gesture has happened.
//
// Design goals: warm, low-volume, mostly sine waves; never above ~0.18 peak
// so it can sit underneath a conversation or background music.

type SoundKind =
  | "roll"
  | "lock"
  | "bank"
  | "bust"
  | "hotdice"
  | "win"
  | "click"
  | "yourTurn"
  | "createRoom"
  | "diceSelect"
  | "diceDeselect";

const STORAGE_KEY = "dicerio.sound";
const MASTER_GAIN = 0.22;

let ctx: AudioContext | null = null;
let masterGain: GainNode | null = null;
let masterFilter: BiquadFilterNode | null = null;
let enabled: boolean = readEnabled();

function readEnabled(): boolean {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw !== "off";
  } catch {
    return true;
  }
}

function ensureContext(): AudioContext | null {
  if (typeof window === "undefined") return null;
  if (!ctx) {
    const AC =
      window.AudioContext ??
      (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AC) return null;
    ctx = new AC();

    masterFilter = ctx.createBiquadFilter();
    masterFilter.type = "lowpass";
    masterFilter.frequency.value = 5200;
    masterFilter.Q.value = 0.4;

    masterGain = ctx.createGain();
    masterGain.gain.value = MASTER_GAIN;

    masterFilter.connect(masterGain);
    masterGain.connect(ctx.destination);
  }
  // Only resume the context when sounds are allowed — otherwise every
  // `primeAudio()` tap on the header would undo `suspend()` from muting.
  if (enabled && ctx.state === "suspended") {
    void ctx.resume();
  }
  return ctx;
}

function destNode(): AudioNode | null {
  ensureContext();
  return masterFilter;
}

export function isSoundEnabled(): boolean {
  return enabled;
}

export function setSoundEnabled(value: boolean): void {
  enabled = value;
  try {
    localStorage.setItem(STORAGE_KEY, value ? "on" : "off");
  } catch {
    /* ignore */
  }

  if (!value) {
    // Hard mute: silence the graph immediately and suspend so nothing
    // (including already-scheduled nodes) reaches the speakers.
    if (ctx && masterGain) {
      masterGain.gain.setValueAtTime(0, ctx.currentTime);
      void ctx.suspend();
    }
    return;
  }

  ensureContext();
  if (ctx && masterGain) {
    masterGain.gain.setValueAtTime(MASTER_GAIN, ctx.currentTime);
    if (ctx.state === "suspended") {
      void ctx.resume();
    }
  }
}

interface ToneOpts {
  freq: number;
  endFreq?: number;
  type?: OscillatorType;
  startAt?: number;
  durationMs: number;
  attackMs?: number;
  releaseMs?: number;
  peak?: number;
}

function tone(c: AudioContext, dest: AudioNode, opts: ToneOpts): void {
  const startAt = opts.startAt ?? c.currentTime;
  const dur = opts.durationMs / 1000;
  const attack = (opts.attackMs ?? 12) / 1000;
  const release = (opts.releaseMs ?? Math.min(180, opts.durationMs * 0.6)) / 1000;
  const peak = opts.peak ?? 0.16;

  const osc = c.createOscillator();
  osc.type = opts.type ?? "sine";
  osc.frequency.setValueAtTime(opts.freq, startAt);
  if (opts.endFreq != null) {
    osc.frequency.exponentialRampToValueAtTime(Math.max(1, opts.endFreq), startAt + dur);
  }

  const gain = c.createGain();
  gain.gain.setValueAtTime(0, startAt);
  gain.gain.linearRampToValueAtTime(peak, startAt + attack);
  // Hold then exponential decay to silence — softer than a hard stop.
  const holdEnd = Math.max(attack, dur - release);
  gain.gain.setValueAtTime(peak, startAt + holdEnd);
  gain.gain.exponentialRampToValueAtTime(0.0001, startAt + dur);

  osc.connect(gain);
  gain.connect(dest);

  osc.start(startAt);
  osc.stop(startAt + dur + 0.05);
}

/**
 * Roll = low sine with fast vibrato on pitch (feels like dice tumbling in a
 * cup). Nothing like a glide, noise burst, or click-train — completely
 * different timbre from every previous iteration.
 */
function playRollWobble(c: AudioContext, dest: AudioNode, startAt: number): void {
  const end = startAt + 0.34;

  const carrier = c.createOscillator();
  carrier.type = "sine";
  carrier.frequency.setValueAtTime(168, startAt);

  const vib = c.createOscillator();
  vib.type = "sine";
  vib.frequency.setValueAtTime(6.2, startAt);
  const vDepth = c.createGain();
  vDepth.gain.setValueAtTime(26, startAt);
  vib.connect(vDepth);
  vDepth.connect(carrier.frequency);

  const env = c.createGain();
  env.gain.setValueAtTime(0, startAt);
  env.gain.linearRampToValueAtTime(0.12, startAt + 0.028);
  env.gain.exponentialRampToValueAtTime(0.0001, end);

  carrier.connect(env);
  env.connect(dest);

  vib.start(startAt);
  carrier.start(startAt);
  carrier.stop(end + 0.02);
  vib.stop(end + 0.02);
}

export function play(kind: SoundKind): void {
  if (!enabled) return;
  const dest = destNode();
  if (!ctx || !dest) return;
  const t = ctx.currentTime;

  switch (kind) {
    case "roll": {
      playRollWobble(ctx, dest, t);
      break;
    }
    case "createRoom": {
      // Tiny upward triad — "room ready".
      const notes = [523.25, 659.25, 783.99];
      notes.forEach((f, i) => {
        tone(ctx!, dest, {
          freq: f,
          type: "sine",
          startAt: t + i * 0.06,
          durationMs: 160,
          attackMs: 5,
          releaseMs: 120,
          peak: 0.095,
        });
      });
      break;
    }
    case "diceSelect": {
      tone(ctx, dest, {
        freq: 698,
        type: "sine",
        durationMs: 42,
        attackMs: 2,
        releaseMs: 32,
        peak: 0.065,
      });
      break;
    }
    case "diceDeselect": {
      tone(ctx, dest, {
        freq: 466,
        type: "sine",
        durationMs: 38,
        attackMs: 2,
        releaseMs: 28,
        peak: 0.055,
      });
      break;
    }
    case "lock": {
      tone(ctx, dest, {
        freq: 720,
        endFreq: 880,
        type: "sine",
        durationMs: 110,
        attackMs: 6,
        releaseMs: 80,
        peak: 0.14,
      });
      break;
    }
    case "bank": {
      // Mellow major-pentatonic ascent (G3, B3, D4, G4) on sine.
      const seq = [196, 247, 294, 392];
      seq.forEach((f, i) => {
        tone(ctx!, dest, {
          freq: f,
          type: "sine",
          startAt: t + i * 0.085,
          durationMs: 220,
          attackMs: 14,
          releaseMs: 180,
          peak: 0.16,
        });
      });
      break;
    }
    case "bust": {
      // Two soft descending sines, no harsh waveforms.
      tone(ctx, dest, {
        freq: 240,
        endFreq: 150,
        type: "sine",
        durationMs: 360,
        attackMs: 18,
        releaseMs: 280,
        peak: 0.16,
      });
      tone(ctx, dest, {
        freq: 180,
        endFreq: 110,
        type: "sine",
        startAt: t + 0.18,
        durationMs: 360,
        attackMs: 18,
        releaseMs: 280,
        peak: 0.12,
      });
      break;
    }
    case "hotdice": {
      // Quick warm fanfare on sine triads (G major over G/B/D/G).
      const notes = [392, 494, 587, 784];
      notes.forEach((f, i) => {
        tone(ctx!, dest, {
          freq: f,
          type: "sine",
          startAt: t + i * 0.06,
          durationMs: 180,
          attackMs: 8,
          releaseMs: 140,
          peak: 0.14,
        });
      });
      break;
    }
    case "win": {
      // Long soft chord (C4/E4/G4/C5) with overlapping tails.
      const chord = [261.63, 329.63, 392, 523.25];
      chord.forEach((f, i) => {
        tone(ctx!, dest, {
          freq: f,
          type: "sine",
          startAt: t + i * 0.09,
          durationMs: 700,
          attackMs: 20,
          releaseMs: 560,
          peak: 0.13,
        });
      });
      break;
    }
    case "yourTurn": {
      // Two-note sine chime (G4 → C5).
      tone(ctx, dest, {
        freq: 392,
        type: "sine",
        durationMs: 140,
        attackMs: 10,
        releaseMs: 110,
        peak: 0.13,
      });
      tone(ctx, dest, {
        freq: 523,
        type: "sine",
        startAt: t + 0.11,
        durationMs: 180,
        attackMs: 10,
        releaseMs: 140,
        peak: 0.13,
      });
      break;
    }
    case "click": {
      tone(ctx, dest, {
        freq: 880,
        type: "sine",
        durationMs: 50,
        attackMs: 4,
        releaseMs: 36,
        peak: 0.08,
      });
      break;
    }
  }
}

/**
 * Best-effort priming on a user gesture so iOS/Safari unlock audio. Call from
 * the first click / pointerdown anywhere in the app.
 */
export function primeAudio(): void {
  if (!enabled) return;
  ensureContext();
}
