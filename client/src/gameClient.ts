import {
  type HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import {
  HubMethods,
  HubServerEvents,
  type CreateRoomResult,
  type HubErrorPayload,
  type JoinRoomResult,
  type MatchStateDto,
} from "./protocol";

export interface GameClientHandlers {
  onState: (state: MatchStateDto) => void;
  onError: (err: HubErrorPayload) => void;
  onMatchEnded: (winnerId: string | null) => void;
  onConnectionChange: (state: HubConnectionState) => void;
}

const HUB_URL = (() => {
  const base = (import.meta.env.VITE_SERVER_URL as string | undefined) ?? "http://localhost:5075";
  return `${base.replace(/\/$/, "")}/hubs/game`;
})();

export class GameClient {
  private connection: HubConnection;
  private handlers: GameClientHandlers;

  constructor(handlers: GameClientHandlers) {
    this.handlers = handlers;
    this.connection = new HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    this.connection.on(HubServerEvents.StateUpdated, (state: MatchStateDto) => {
      handlers.onState(state);
    });
    this.connection.on(HubServerEvents.Error, (payload: HubErrorPayload) => {
      handlers.onError(payload);
    });
    this.connection.on(HubServerEvents.MatchEnded, (winnerId: string | null) => {
      handlers.onMatchEnded(winnerId);
    });

    this.connection.onreconnecting(() => handlers.onConnectionChange(HubConnectionState.Reconnecting));
    this.connection.onreconnected(() => handlers.onConnectionChange(HubConnectionState.Connected));
    this.connection.onclose(() => handlers.onConnectionChange(HubConnectionState.Disconnected));
  }

  get state(): HubConnectionState {
    return this.connection.state;
  }

  async ensureStarted(): Promise<void> {
    if (this.connection.state === HubConnectionState.Disconnected) {
      this.handlers.onConnectionChange(HubConnectionState.Connecting);
      await this.connection.start();
      this.handlers.onConnectionChange(this.connection.state);
    }
  }

  async createRoom(displayName: string | undefined, targetScore: number | undefined): Promise<CreateRoomResult> {
    await this.ensureStarted();
    return this.connection.invoke<CreateRoomResult>(HubMethods.CreateRoom, {
      displayName: displayName ?? null,
      targetScore: targetScore ?? null,
    });
  }

  async joinRoom(roomCode: string, displayName: string | undefined): Promise<JoinRoomResult> {
    await this.ensureStarted();
    return this.connection.invoke<JoinRoomResult>(HubMethods.JoinRoom, {
      roomCode,
      displayName: displayName ?? null,
    });
  }

  async roll(): Promise<void> {
    await this.connection.invoke(HubMethods.RollAgain);
  }

  async submitLock(diceIndexes: number[]): Promise<void> {
    await this.connection.invoke(HubMethods.SubmitLock, diceIndexes);
  }

  async bank(): Promise<void> {
    await this.connection.invoke(HubMethods.Bank);
  }

  async leave(): Promise<void> {
    if (this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke(HubMethods.LeaveRoom);
    }
  }

  async stop(): Promise<void> {
    await this.connection.stop();
  }
}
