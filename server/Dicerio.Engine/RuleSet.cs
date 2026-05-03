namespace Dicerio.Engine;

/// <summary>
/// Configuration for a Farkle ruleset. v1 uses a single fixed ruleset id but
/// the type is here so the protocol carries an explicit version forward.
/// </summary>
public sealed record RuleSet(
    string Id,
    int TargetScore,
    int DiceCount,
    bool HotDiceReroll,
    bool AllowStraights)
{
    public static readonly RuleSet V1 = new(
        Id: "v1",
        TargetScore: 3_000,
        DiceCount: 6,
        HotDiceReroll: true,
        AllowStraights: true);

    public RuleSet WithTargetScore(int targetScore) => this with { TargetScore = targetScore };
}
