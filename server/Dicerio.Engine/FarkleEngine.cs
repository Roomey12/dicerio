namespace Dicerio.Engine;

/// <summary>
/// Pure-ish state machine for a multiplayer Farkle match (2–5 players). Methods take the current
/// <see cref="MatchState"/> plus an action and return a new state or an error.
/// All randomness flows through <see cref="IDiceRoller"/> for testability.
/// </summary>
public static class FarkleEngine
{
    public const int MinPlayers = 2;
    public const int MaxPlayersAllowed = 5;

    public static MatchState CreateMatch(
        string matchId,
        string roomCode,
        RuleSet rules,
        Player host,
        int maxPlayers = MinPlayers,
        DateTime? startedAtUtc = null)
    {
        var dice = new Die[rules.DiceCount];
        Array.Fill(dice, Die.Empty);
        var cap = Math.Clamp(maxPlayers, MinPlayers, MaxPlayersAllowed);

        return new MatchState(
            MatchId: matchId,
            RoomCode: roomCode,
            Rules: rules,
            MaxPlayers: cap,
            Players: [host with { Seat = 0 }],
            ActivePlayerId: null,
            Phase: MatchPhase.WaitingForOpponent,
            Dice: dice,
            TurnScore: 0,
            WinnerId: null,
            LastEvent: new LastEvent(LastEventKind.PlayerJoined, host.PlayerId, null, $"{host.DisplayName} created the room"),
            Version: 1)
        {
            StartedAtUtc = startedAtUtc ?? DateTime.UtcNow,
        };
    }

    public static EngineResult AddOpponent(MatchState state, Player opponent, IDiceRoller roller)
    {
        if (state.Phase != MatchPhase.WaitingForOpponent)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Match is not accepting joins.");
        }

        if (state.Players.Count >= state.MaxPlayers)
        {
            return EngineResult.Fail(EngineErrorCode.RoomFull, "Room is full.");
        }

        var seat = state.Players.Count;
        var joined = opponent with { Seat = seat };
        var players = new List<Player>(state.Players) { joined };

        if (players.Count == state.MaxPlayers)
        {
            var firstSeat = roller.Next(players.Count);
            var activePlayerId = players[firstSeat].PlayerId;

            return EngineResult.Ok(state with
            {
                Players = players,
                ActivePlayerId = activePlayerId,
                Phase = MatchPhase.AwaitingRoll,
                Dice = FreshDice(state.Rules.DiceCount),
                TurnScore = 0,
                LastEvent = new LastEvent(LastEventKind.MatchStarted, activePlayerId, null, "Match started"),
                Version = state.Version + 1,
            });
        }

        return EngineResult.Ok(state with
        {
            Players = players,
            ActivePlayerId = null,
            Phase = MatchPhase.WaitingForOpponent,
            LastEvent = new LastEvent(LastEventKind.PlayerJoined, joined.PlayerId, null, $"{joined.DisplayName} joined"),
            Version = state.Version + 1,
        });
    }

    /// <summary>
    /// Host-only: start the match early when at least two players have joined but the room is not full.
    /// </summary>
    public static EngineResult StartLobbyMatch(MatchState state, string hostPlayerId, IDiceRoller roller)
    {
        if (state.Phase != MatchPhase.WaitingForOpponent)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Match has already started.");
        }

        if (state.Players.Count == 0 || state.Players[0].PlayerId != hostPlayerId)
        {
            return EngineResult.Fail(EngineErrorCode.NotHost, "Only the host can start the match.");
        }

        if (state.Players.Count < MinPlayers)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Need at least two players to start.");
        }

        if (state.Players.Count == state.MaxPlayers)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Room is already full; match should have started.");
        }

        var players = state.Players.ToList();
        var firstSeat = roller.Next(players.Count);
        var activePlayerId = players[firstSeat].PlayerId;

        return EngineResult.Ok(state with
        {
            ActivePlayerId = activePlayerId,
            Phase = MatchPhase.AwaitingRoll,
            Dice = FreshDice(state.Rules.DiceCount),
            TurnScore = 0,
            LastEvent = new LastEvent(LastEventKind.MatchStarted, activePlayerId, null, "Match started"),
            Version = state.Version + 1,
        });
    }

    /// <summary>
    /// Host-only after <see cref="MatchPhase.GameOver"/>: same players and room, scores reset, new random first seat.
    /// </summary>
    public static EngineResult Rematch(MatchState state, string hostPlayerId, IDiceRoller roller)
    {
        if (state.Phase != MatchPhase.GameOver)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Match is not over.");
        }

        if (state.Players.Count == 0 || state.Players[0].PlayerId != hostPlayerId)
        {
            return EngineResult.Fail(EngineErrorCode.NotHost, "Only the host can start a rematch.");
        }

        var resetPlayers = state.Players.Select(p => p with { MatchScore = 0 }).ToList();
        var firstSeat = roller.Next(resetPlayers.Count);
        var activePlayerId = resetPlayers[firstSeat].PlayerId;

        return EngineResult.Ok(state with
        {
            Players = resetPlayers,
            WinnerId = null,
            TurnScore = 0,
            Dice = FreshDice(state.Rules.DiceCount),
            ActivePlayerId = activePlayerId,
            Phase = MatchPhase.AwaitingRoll,
            LastEvent = new LastEvent(LastEventKind.MatchStarted, activePlayerId, null, "Rematch started"),
            Version = state.Version + 1,
        });
    }

    /// <summary>
    /// Remove a player from the pre-game lobby and renumber seats. Used when a guest leaves voluntarily.
    /// </summary>
    public static EngineResult LeaveLobby(MatchState state, string playerId)
    {
        if (state.Phase != MatchPhase.WaitingForOpponent)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Not in the lobby.");
        }

        if (state.GetPlayer(playerId) is null)
        {
            return EngineResult.Fail(EngineErrorCode.UnknownPlayer, "Unknown player.");
        }

        var remaining = state.Players
            .Where(p => p.PlayerId != playerId)
            .Select((p, i) => p with { Seat = i })
            .ToList();

        var leaver = state.GetPlayer(playerId)!;
        return EngineResult.Ok(state with
        {
            Players = remaining,
            LastEvent = new LastEvent(LastEventKind.None, playerId, null, $"{leaver.DisplayName} left"),
            Version = state.Version + 1,
        });
    }

    public static EngineResult Roll(MatchState state, string playerId, IDiceRoller roller)
    {
        if (state.Phase == MatchPhase.GameOver)
        {
            return EngineResult.Fail(EngineErrorCode.MatchAlreadyOver, "Match is over.");
        }

        if (state.Phase == MatchPhase.BustReveal)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Bust is being revealed; wait a moment.");
        }

        if (state.Phase != MatchPhase.AwaitingRoll)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Not in a roll phase.");
        }

        if (state.ActivePlayerId != playerId)
        {
            return EngineResult.Fail(EngineErrorCode.NotYourTurn, "Not your turn.");
        }

        var dice = state.Dice.ToArray();
        var rolledFaces = new List<int>(dice.Length);

        for (var i = 0; i < dice.Length; i++)
        {
            if (!dice[i].Locked)
            {
                var v = roller.Roll();
                dice[i] = new Die(v, false);
                rolledFaces.Add(v);
            }
        }

        if (!Scoring.HasAnyScoring(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rolledFaces)))
        {
            return EngineResult.Ok(BustAndPass(state, dice, playerId));
        }

        return EngineResult.Ok(state with
        {
            Dice = dice,
            Phase = MatchPhase.AwaitingLock,
            LastEvent = new LastEvent(LastEventKind.Rolled, playerId, null, null),
            Version = state.Version + 1,
        });
    }

    public static EngineResult SubmitLock(MatchState state, string playerId, IReadOnlyList<int> diceIndexes)
    {
        if (state.Phase == MatchPhase.GameOver)
        {
            return EngineResult.Fail(EngineErrorCode.MatchAlreadyOver, "Match is over.");
        }

        if (state.Phase == MatchPhase.BustReveal)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Bust is being revealed; wait a moment.");
        }

        if (state.Phase != MatchPhase.AwaitingLock)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Not in a lock phase.");
        }

        if (state.ActivePlayerId != playerId)
        {
            return EngineResult.Fail(EngineErrorCode.NotYourTurn, "Not your turn.");
        }

        if (diceIndexes.Count == 0)
        {
            return EngineResult.Fail(EngineErrorCode.EmptyLock, "Must lock at least one scoring die.");
        }

        var seen = new HashSet<int>();
        foreach (var idx in diceIndexes)
        {
            if (idx < 0 || idx >= state.Dice.Count)
            {
                return EngineResult.Fail(EngineErrorCode.InvalidLockIndex, $"Index {idx} out of range.");
            }

            if (!seen.Add(idx))
            {
                return EngineResult.Fail(EngineErrorCode.InvalidLockIndex, $"Duplicate index {idx}.");
            }

            if (state.Dice[idx].Locked)
            {
                return EngineResult.Fail(EngineErrorCode.InvalidLockIndex, $"Die {idx} is already locked.");
            }
        }

        Span<int> faces = stackalloc int[diceIndexes.Count];
        for (var i = 0; i < diceIndexes.Count; i++)
        {
            faces[i] = state.Dice[diceIndexes[i]].Value;
        }

        var score = Scoring.ScoreLock(faces);
        if (score is null)
        {
            return EngineResult.Fail(EngineErrorCode.InvalidLockPartition,
                "Locked dice do not form a valid scoring partition.");
        }

        var newDice = state.Dice.ToArray();
        foreach (var idx in diceIndexes)
        {
            newDice[idx] = newDice[idx] with { Locked = true };
        }

        var allLocked = newDice.All(d => d.Locked);
        var newTurnScore = state.TurnScore + score.Points;

        if (allLocked && state.Rules.HotDiceReroll)
        {
            for (var i = 0; i < newDice.Length; i++)
            {
                newDice[i] = Die.Empty;
            }

            return EngineResult.Ok(state with
            {
                Dice = newDice,
                TurnScore = newTurnScore,
                Phase = MatchPhase.AwaitingRoll,
                LastEvent = new LastEvent(LastEventKind.HotDice, playerId, score.Points, "Hot dice — reroll all six"),
                Version = state.Version + 1,
            });
        }

        return EngineResult.Ok(state with
        {
            Dice = newDice,
            TurnScore = newTurnScore,
            Phase = MatchPhase.AwaitingRoll,
            LastEvent = new LastEvent(LastEventKind.Locked, playerId, score.Points, null),
            Version = state.Version + 1,
        });
    }

    public static EngineResult Bank(MatchState state, string playerId)
    {
        if (state.Phase == MatchPhase.GameOver)
        {
            return EngineResult.Fail(EngineErrorCode.MatchAlreadyOver, "Match is over.");
        }

        if (state.Phase == MatchPhase.BustReveal)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Bust is being revealed; wait a moment.");
        }

        if (state.Phase != MatchPhase.AwaitingRoll)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Bank only allowed after a successful lock.");
        }

        if (state.ActivePlayerId != playerId)
        {
            return EngineResult.Fail(EngineErrorCode.NotYourTurn, "Not your turn.");
        }

        if (state.TurnScore <= 0)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Nothing to bank yet.");
        }

        var banked = state.TurnScore;
        var newPlayers = state.Players
            .Select(p => p.PlayerId == playerId ? p with { MatchScore = p.MatchScore + banked } : p)
            .ToList();

        var actor = newPlayers.First(p => p.PlayerId == playerId);
        if (actor.MatchScore >= state.Rules.TargetScore)
        {
            return EngineResult.Ok(state with
            {
                Players = newPlayers,
                TurnScore = 0,
                Dice = FreshDice(state.Rules.DiceCount),
                Phase = MatchPhase.GameOver,
                WinnerId = playerId,
                LastEvent = new LastEvent(LastEventKind.GameOver, playerId, banked, $"{actor.DisplayName} wins"),
                Version = state.Version + 1,
            });
        }

        var nextActive = NextActivePlayerId(state, playerId);
        return EngineResult.Ok(state with
        {
            Players = newPlayers,
            TurnScore = 0,
            Dice = FreshDice(state.Rules.DiceCount),
            ActivePlayerId = nextActive,
            Phase = MatchPhase.AwaitingRoll,
            LastEvent = new LastEvent(LastEventKind.Banked, playerId, banked, null),
            Version = state.Version + 1,
        });
    }

    public static EngineResult Forfeit(MatchState state, string playerId, string reason)
    {
        if (state.Phase == MatchPhase.GameOver)
        {
            return EngineResult.Fail(EngineErrorCode.MatchAlreadyOver, "Match is already over.");
        }

        var loser = state.GetPlayer(playerId);
        if (loser is null)
        {
            return EngineResult.Fail(EngineErrorCode.UnknownPlayer, "Unknown player.");
        }

        var winnerId = WinnerAfterForfeit(state, playerId);
        return EngineResult.Ok(state with
        {
            Phase = MatchPhase.GameOver,
            WinnerId = winnerId,
            TurnScore = 0,
            LastEvent = new LastEvent(LastEventKind.Forfeit, playerId, null, reason),
            Version = state.Version + 1,
        });
    }

    /// <summary>
    /// Auto-advance from <see cref="MatchPhase.BustReveal"/> to the opponent's
    /// turn. The hub schedules this after a short delay so both clients see the
    /// busted faces. Idempotent if the match has moved on (e.g. forfeit).
    ///
    /// <see cref="MatchState.LastEvent"/> is reset to <see cref="LastEventKind.None"/>
    /// because the bust event was already announced on the BustReveal broadcast;
    /// carrying it forward here would cause clients to log it twice (and with
    /// the dice already cleared the second log would render with no values).
    /// </summary>
    public static EngineResult AcknowledgeBust(MatchState state)
    {
        if (state.Phase != MatchPhase.BustReveal)
        {
            return EngineResult.Fail(EngineErrorCode.WrongPhase, "Not in a bust reveal.");
        }

        var bustedPlayerId = state.ActivePlayerId
            ?? throw new InvalidOperationException("BustReveal without an active player.");
        var nextActive = NextActivePlayerId(state, bustedPlayerId);

        return EngineResult.Ok(state with
        {
            Dice = FreshDice(state.Rules.DiceCount),
            ActivePlayerId = nextActive,
            Phase = MatchPhase.AwaitingRoll,
            LastEvent = new LastEvent(LastEventKind.None, null, null, null),
            Version = state.Version + 1,
        });
    }

    private static string NextActivePlayerId(MatchState state, string currentPlayerId)
    {
        var ordered = state.Players.OrderBy(p => p.Seat).ToList();
        var i = ordered.FindIndex(p => p.PlayerId == currentPlayerId);
        if (i < 0)
        {
            return ordered[0].PlayerId;
        }

        return ordered[(i + 1) % ordered.Count].PlayerId;
    }

    private static string? WinnerAfterForfeit(MatchState state, string forfeiterId)
    {
        var rest = state.Players.Where(p => p.PlayerId != forfeiterId).ToList();
        if (rest.Count == 0)
        {
            return null;
        }

        return rest
            .OrderByDescending(p => p.MatchScore)
            .ThenBy(p => p.Seat)
            .First()
            .PlayerId;
    }

    private static MatchState BustAndPass(MatchState state, Die[] currentDice, string playerId)
    {
        // Keep the busted player as the active player during BustReveal so the UI
        // can render "X busted" with the highlight on the offender. The hub will
        // schedule AcknowledgeBust to advance to the opponent after a short delay.
        return state with
        {
            Dice = currentDice,
            TurnScore = 0,
            Phase = MatchPhase.BustReveal,
            LastEvent = new LastEvent(LastEventKind.Busted, playerId, null, null),
            Version = state.Version + 1,
        };
    }

    private static Die[] FreshDice(int count)
    {
        var dice = new Die[count];
        Array.Fill(dice, Die.Empty);
        return dice;
    }
}
