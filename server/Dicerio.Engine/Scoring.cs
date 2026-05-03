namespace Dicerio.Engine;

public enum ComboKind
{
    Single,
    ThreeOfAKind,
    FourOfAKind,
    FiveOfAKind,
    SixOfAKind,
    Straight
}

public sealed record ScoredCombo(ComboKind Kind, int Face, int Count, int Points);

public sealed record ScoreResult(int Points, IReadOnlyList<ScoredCombo> Combos)
{
    public static readonly ScoreResult Zero = new(0, []);
}

/// <summary>
/// Pure Farkle scoring + bust detection. Operates on multisets of face values 1..6.
/// All inputs are validated and assumed to be legal die faces; out-of-range faces throw.
/// </summary>
public static class Scoring
{
    public const int FullStraightPoints = 1500;

    /// <summary>
    /// Find the maximum scoring partition of <paramref name="faces"/> where every die
    /// belongs to exactly one scoring combination. Returns <c>null</c> if no full
    /// partition exists (i.e. at least one die would be orphaned).
    /// </summary>
    public static ScoreResult? ScoreLock(ReadOnlySpan<int> faces)
    {
        if (faces.Length == 0)
        {
            return null;
        }

        Span<int> counts = stackalloc int[7];
        foreach (var f in faces)
        {
            if (f is < 1 or > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(faces), f, "Die faces must be in 1..6.");
            }

            counts[f]++;
        }

        return ScoreCounts(counts);
    }

    /// <summary>
    /// True if the given rolled dice contain at least one valid scoring subset.
    /// Used to detect bust.
    /// </summary>
    public static bool HasAnyScoring(ReadOnlySpan<int> rolled)
    {
        if (rolled.Length == 0)
        {
            return false;
        }

        Span<int> counts = stackalloc int[7];
        foreach (var f in rolled)
        {
            if (f is < 1 or > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(rolled), f, "Die faces must be in 1..6.");
            }

            counts[f]++;
        }

        if (counts[1] > 0 || counts[5] > 0)
        {
            return true;
        }

        for (var face = 1; face <= 6; face++)
        {
            if (counts[face] >= 3)
            {
                return true;
            }
        }

        if (rolled.Length == 6 && IsFullStraight(counts))
        {
            return true;
        }

        return false;
    }

    private static bool IsFullStraight(ReadOnlySpan<int> counts)
    {
        for (var face = 1; face <= 6; face++)
        {
            if (counts[face] != 1)
            {
                return false;
            }
        }

        return true;
    }

    private static ScoreResult? ScoreCounts(ReadOnlySpan<int> counts)
    {
        var total = 0;
        for (var face = 1; face <= 6; face++)
        {
            total += counts[face];
        }

        if (total == 6 && IsFullStraight(counts))
        {
            return new ScoreResult(FullStraightPoints, [new ScoredCombo(ComboKind.Straight, 0, 6, FullStraightPoints)]);
        }

        var combos = new List<ScoredCombo>(6);
        var points = 0;

        for (var face = 1; face <= 6; face++)
        {
            var n = counts[face];
            if (n == 0)
            {
                continue;
            }

            if (n >= 3)
            {
                var (kind, comboPoints) = NOfAKind(face, n);
                combos.Add(new ScoredCombo(kind, face, n, comboPoints));
                points += comboPoints;
            }
            else
            {
                if (face == 1)
                {
                    points += 100 * n;
                    for (var i = 0; i < n; i++)
                    {
                        combos.Add(new ScoredCombo(ComboKind.Single, 1, 1, 100));
                    }
                }
                else if (face == 5)
                {
                    points += 50 * n;
                    for (var i = 0; i < n; i++)
                    {
                        combos.Add(new ScoredCombo(ComboKind.Single, 5, 1, 50));
                    }
                }
                else
                {
                    return null;
                }
            }
        }

        return new ScoreResult(points, combos);
    }

    private static (ComboKind Kind, int Points) NOfAKind(int face, int n)
    {
        var basePoints = face == 1 ? 1000 : face * 100;
        return n switch
        {
            3 => (ComboKind.ThreeOfAKind, basePoints),
            4 => (ComboKind.FourOfAKind, basePoints * 2),
            5 => (ComboKind.FiveOfAKind, basePoints * 4),
            6 => (ComboKind.SixOfAKind, basePoints * 8),
            _ => throw new InvalidOperationException($"Unsupported n-of-a-kind count: {n}"),
        };
    }
}
