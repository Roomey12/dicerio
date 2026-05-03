using Dicerio.Engine;

namespace Dicerio.Engine.Tests;

public sealed class EngineTests
{
    private static readonly Player Alice = new("p-alice", "Alice", 0, 0);
    private static readonly Player Bob = new("p-bob", "Bob", 1, 0);

    private static MatchState NewStartedMatch(IDiceRoller? roller = null, RuleSet? rules = null)
    {
        rules ??= RuleSet.V1;
        var state = FarkleEngine.CreateMatch("m1", "ABC123", rules, Alice);
        var add = FarkleEngine.AddOpponent(state, Bob, roller ?? new ScriptedRoller(Array.Empty<int>(), new[] { 0 }));
        Assert.True(add.IsOk);
        return add.State!;
    }

    [Fact]
    public void CreateMatch_starts_in_WaitingForOpponent()
    {
        var s = FarkleEngine.CreateMatch("m1", "ABC123", RuleSet.V1, Alice);
        Assert.Equal(MatchPhase.WaitingForOpponent, s.Phase);
        Assert.Single(s.Players);
        Assert.Null(s.ActivePlayerId);
    }

    [Fact]
    public void AddOpponent_starts_match_and_chooses_first_player_via_roller()
    {
        var roller = new ScriptedRoller(Array.Empty<int>(), new[] { 1 });
        var s = FarkleEngine.CreateMatch("m1", "ABC123", RuleSet.V1, Alice);
        var r = FarkleEngine.AddOpponent(s, Bob, roller);
        Assert.True(r.IsOk);
        Assert.Equal(MatchPhase.AwaitingRoll, r.State!.Phase);
        Assert.Equal(Bob.PlayerId, r.State.ActivePlayerId);
    }

    [Fact]
    public void AddOpponent_when_match_full_fails()
    {
        var s = NewStartedMatch();
        var r = FarkleEngine.AddOpponent(s, new Player("p-c", "Carol", 2, 0), new ScriptedRoller(Array.Empty<int>()));
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.WrongPhase, r.Error!.Code);
    }

    [Fact]
    public void Roll_not_your_turn_fails()
    {
        var s = NewStartedMatch();
        var notActive = s.ActivePlayerId == Alice.PlayerId ? Bob.PlayerId : Alice.PlayerId;
        var r = FarkleEngine.Roll(s, notActive, new ScriptedRoller(new[] { 1, 2, 3, 4, 5, 6 }));
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.NotYourTurn, r.Error!.Code);
    }

    [Fact]
    public void Roll_with_no_scoring_enters_BustReveal_with_dice_visible()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var roller = new ScriptedRoller(new[] { 2, 3, 4, 6, 4, 2 });
        var r = FarkleEngine.Roll(s, active, roller);
        Assert.True(r.IsOk);
        Assert.Equal(MatchPhase.BustReveal, r.State!.Phase);
        Assert.Equal(active, r.State.ActivePlayerId);
        Assert.Equal(0, r.State.TurnScore);
        Assert.Equal(LastEventKind.Busted, r.State.LastEvent.Kind);
        Assert.Equal(active, r.State.LastEvent.PlayerId);
        Assert.Equal(new[] { 2, 3, 4, 6, 4, 2 }, r.State.Dice.Select(d => d.Value).ToArray());
    }

    [Fact]
    public void AcknowledgeBust_passes_turn_and_clears_dice()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var bust = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 2, 3, 4, 6, 4, 2 })).State!;

        var ack = FarkleEngine.AcknowledgeBust(bust);
        Assert.True(ack.IsOk);
        Assert.Equal(MatchPhase.AwaitingRoll, ack.State!.Phase);
        Assert.NotEqual(active, ack.State.ActivePlayerId);
        Assert.All(ack.State.Dice, d => Assert.Equal(0, d.Value));
    }

    [Fact]
    public void AcknowledgeBust_resets_LastEvent_so_clients_dont_double_log()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var bust = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 2, 3, 4, 6, 4, 2 })).State!;
        Assert.Equal(LastEventKind.Busted, bust.LastEvent.Kind);

        var ack = FarkleEngine.AcknowledgeBust(bust);
        Assert.True(ack.IsOk);
        Assert.Equal(LastEventKind.None, ack.State!.LastEvent.Kind);
        Assert.Null(ack.State.LastEvent.PlayerId);
    }

    [Fact]
    public void AcknowledgeBust_outside_BustReveal_fails()
    {
        var s = NewStartedMatch();
        var r = FarkleEngine.AcknowledgeBust(s);
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.WrongPhase, r.Error!.Code);
    }

    [Fact]
    public void Actions_during_BustReveal_are_rejected()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var bust = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 2, 3, 4, 6, 4, 2 })).State!;

        Assert.Equal(EngineErrorCode.WrongPhase,
            FarkleEngine.Roll(bust, active, new ScriptedRoller(new[] { 1, 1, 1, 1, 1, 1 })).Error!.Code);
        Assert.Equal(EngineErrorCode.WrongPhase,
            FarkleEngine.SubmitLock(bust, active, new[] { 0 }).Error!.Code);
        Assert.Equal(EngineErrorCode.WrongPhase, FarkleEngine.Bank(bust, active).Error!.Code);
    }

    [Fact]
    public void Roll_with_scoring_transitions_to_AwaitingLock()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var r = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 6, 6 }));
        Assert.True(r.IsOk);
        Assert.Equal(MatchPhase.AwaitingLock, r.State!.Phase);
        Assert.Equal(LastEventKind.Rolled, r.State.LastEvent.Kind);
    }

    [Fact]
    public void Lock_with_invalid_partition_fails_and_state_unchanged()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 6, 6 })).State!;

        var r = FarkleEngine.SubmitLock(rolled, active, new[] { 0, 1 });
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.InvalidLockPartition, r.Error!.Code);
    }

    [Fact]
    public void Lock_with_already_locked_index_fails()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 5, 6 })).State!;
        var locked = FarkleEngine.SubmitLock(rolled, active, new[] { 0 }).State!;

        var r = FarkleEngine.SubmitLock(locked, active, new[] { 0 });
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.WrongPhase, r.Error!.Code);
    }

    [Fact]
    public void Lock_must_be_non_empty()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 5, 6 })).State!;
        var r = FarkleEngine.SubmitLock(rolled, active, Array.Empty<int>());
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.EmptyLock, r.Error!.Code);
    }

    [Fact]
    public void Lock_rejects_duplicate_index()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 5, 6 })).State!;
        var r = FarkleEngine.SubmitLock(rolled, active, new[] { 0, 0 });
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.InvalidLockIndex, r.Error!.Code);
    }

    [Fact]
    public void Lock_accumulates_turn_score_and_returns_to_AwaitingRoll()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 6, 6 })).State!;
        var r = FarkleEngine.SubmitLock(rolled, active, new[] { 0 });
        Assert.True(r.IsOk);
        Assert.Equal(MatchPhase.AwaitingRoll, r.State!.Phase);
        Assert.Equal(100, r.State.TurnScore);
        Assert.True(r.State.Dice[0].Locked);
    }

    [Fact]
    public void Lock_full_partition_six_dice_triggers_HotDice()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 5, 6 })).State!;
        var r = FarkleEngine.SubmitLock(rolled, active, new[] { 0, 1, 2, 3, 4, 5 });
        Assert.True(r.IsOk);
        Assert.Equal(MatchPhase.AwaitingRoll, r.State!.Phase);
        Assert.Equal(LastEventKind.HotDice, r.State.LastEvent.Kind);
        Assert.All(r.State.Dice, d => Assert.False(d.Locked));
        Assert.Equal(1500, r.State.TurnScore);
    }

    [Fact]
    public void Bank_with_no_turn_score_is_rejected()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var r = FarkleEngine.Bank(s, active);
        Assert.False(r.IsOk);
    }

    [Fact]
    public void Bank_adds_turn_score_to_match_and_passes_turn()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 6, 6 })).State!;
        var locked = FarkleEngine.SubmitLock(rolled, active, new[] { 0 }).State!;
        var r = FarkleEngine.Bank(locked, active);

        Assert.True(r.IsOk);
        var actor = r.State!.GetPlayer(active)!;
        Assert.Equal(100, actor.MatchScore);
        Assert.Equal(0, r.State.TurnScore);
        Assert.NotEqual(active, r.State.ActivePlayerId);
        Assert.Equal(LastEventKind.Banked, r.State.LastEvent.Kind);
    }

    [Fact]
    public void Bank_at_target_score_ends_match()
    {
        var rules = RuleSet.V1.WithTargetScore(1000);
        var s = NewStartedMatch(rules: rules);
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 1, 1, 2, 3, 4 })).State!;
        var locked = FarkleEngine.SubmitLock(rolled, active, new[] { 0, 1, 2 }).State!;
        var r = FarkleEngine.Bank(locked, active);

        Assert.True(r.IsOk);
        Assert.Equal(MatchPhase.GameOver, r.State!.Phase);
        Assert.Equal(active, r.State.WinnerId);
        Assert.Equal(LastEventKind.GameOver, r.State.LastEvent.Kind);
    }

    [Fact]
    public void Bust_loses_only_turn_score_not_match_score()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;

        var rolled1 = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 6, 6 })).State!;
        var locked = FarkleEngine.SubmitLock(rolled1, active, new[] { 0 }).State!;
        var banked = FarkleEngine.Bank(locked, active).State!;

        var nextActive = banked.ActivePlayerId!;
        var rolled2 = FarkleEngine.Roll(banked, nextActive, new ScriptedRoller(new[] { 1, 2, 3, 4, 6, 6 })).State!;
        var locked2 = FarkleEngine.SubmitLock(rolled2, nextActive, new[] { 0 }).State!;
        var bust = FarkleEngine.Roll(locked2, nextActive, new ScriptedRoller(new[] { 2, 3, 4, 6, 4 })).State!;
        var passed = FarkleEngine.AcknowledgeBust(bust).State!;

        Assert.Equal(MatchPhase.AwaitingRoll, passed.Phase);
        Assert.Equal(0, passed.TurnScore);
        Assert.Equal(LastEventKind.Busted, bust.LastEvent.Kind);
        Assert.Equal(100, passed.GetPlayer(active)!.MatchScore);
        Assert.Equal(0, passed.GetPlayer(nextActive)!.MatchScore);
        Assert.Equal(active, passed.ActivePlayerId);
    }

    [Fact]
    public void Actions_after_GameOver_fail()
    {
        var rules = RuleSet.V1.WithTargetScore(1000);
        var s = NewStartedMatch(rules: rules);
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 1, 1, 2, 3, 4 })).State!;
        var locked = FarkleEngine.SubmitLock(rolled, active, new[] { 0, 1, 2 }).State!;
        var ended = FarkleEngine.Bank(locked, active).State!;

        Assert.Equal(EngineErrorCode.MatchAlreadyOver,
            FarkleEngine.Roll(ended, active, new ScriptedRoller(new[] { 1, 1, 1, 1, 1, 1 })).Error!.Code);
        Assert.Equal(EngineErrorCode.MatchAlreadyOver,
            FarkleEngine.Bank(ended, active).Error!.Code);
        Assert.Equal(EngineErrorCode.MatchAlreadyOver,
            FarkleEngine.SubmitLock(ended, active, new[] { 0 }).Error!.Code);
    }

    [Fact]
    public void Forfeit_makes_opponent_winner()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var r = FarkleEngine.Forfeit(s, active, "disconnected");
        Assert.True(r.IsOk);
        Assert.Equal(MatchPhase.GameOver, r.State!.Phase);
        Assert.NotEqual(active, r.State.WinnerId);
        Assert.Equal(LastEventKind.Forfeit, r.State.LastEvent.Kind);
    }

    [Fact]
    public void Lock_after_HotDice_requires_new_roll_first()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 5, 6 })).State!;
        var hot = FarkleEngine.SubmitLock(rolled, active, new[] { 0, 1, 2, 3, 4, 5 }).State!;

        var r = FarkleEngine.SubmitLock(hot, active, new[] { 0 });
        Assert.False(r.IsOk);
        Assert.Equal(EngineErrorCode.WrongPhase, r.Error!.Code);
    }

    [Fact]
    public void Roll_only_unlocked_dice()
    {
        var s = NewStartedMatch();
        var active = s.ActivePlayerId!;
        var rolled = FarkleEngine.Roll(s, active, new ScriptedRoller(new[] { 1, 2, 3, 4, 6, 6 })).State!;
        var locked = FarkleEngine.SubmitLock(rolled, active, new[] { 0 }).State!;

        var roller = new ScriptedRoller(new[] { 5, 5, 5, 5, 5 });
        var r2 = FarkleEngine.Roll(locked, active, roller);
        Assert.True(r2.IsOk);
        Assert.Equal(0, roller.Remaining);
        Assert.Equal(1, r2.State!.Dice[0].Value);
        Assert.True(r2.State.Dice[0].Locked);
    }
}
