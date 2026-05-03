// Mirror of the server-side DTOs and event names. Keep these literally in sync
// with Dicerio.Server/Hubs/Dtos.cs and GameHub.cs.

export type MatchPhase =
  | "WaitingForOpponent"
  | "AwaitingRoll"
  | "AwaitingLock"
  | "BustReveal"
  | "GameOver";

export type LastEventKind =
  | "None"
  | "PlayerJoined"
  | "MatchStarted"
  | "Rolled"
  | "Locked"
  | "Banked"
  | "Busted"
  | "HotDice"
  | "GameOver"
  | "Forfeit";

export interface PlayerDto {
  playerId: string;
  displayName: string;
  seat: number;
  matchScore: number;
  connected: boolean;
}

export interface DiceDto {
  value: number;
  locked: boolean;
  hintPoints: number | null;
  hintLabel: string | null;
}

export interface RuleSetDto {
  id: string;
  targetScore: number;
  diceCount: number;
  hotDiceReroll: boolean;
  allowStraights: boolean;
}

export interface LastEventDto {
  kind: LastEventKind;
  playerId: string | null;
  points: number | null;
  message: string | null;
}

export interface MatchStateDto {
  matchId: string;
  roomCode: string;
  rules: RuleSetDto;
  maxPlayers: number;
  hostPlayerId: string | null;
  players: PlayerDto[];
  activePlayerId: string | null;
  phase: MatchPhase;
  dice: DiceDto[];
  turnScore: number;
  winnerId: string | null;
  lastEvent: LastEventDto;
  version: number;
  /** Server-stamped milliseconds since the match was created. Both clients see
   *  the same value for the same broadcast, so history rows align across tabs. */
  elapsedMs: number;
  youAre: string | null;
  /** Die indices the active player is highlighting before Lock (AwaitingLock only). */
  activePlayerPendingLockIndexes: number[] | null;
}

export interface CreateRoomResult {
  roomCode: string;
  matchId: string;
  playerId: string;
}

export interface JoinRoomResult {
  roomCode: string;
  matchId: string;
  playerId: string;
}

export interface HubErrorPayload {
  code: string;
  message: string;
}

export const HubServerEvents = {
  StateUpdated: "StateUpdated",
  Error: "GameError",
  MatchEnded: "MatchEnded",
} as const;

export const HubMethods = {
  CreateRoom: "CreateRoom",
  JoinRoom: "JoinRoom",
  RollAgain: "RollAgain",
  SubmitLock: "SubmitLock",
  Bank: "Bank",
  LeaveRoom: "LeaveRoom",
  PreviewLock: "PreviewLock",
  StartMatch: "StartMatch",
  PlayAgain: "PlayAgain",
} as const;
