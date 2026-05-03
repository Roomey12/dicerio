using Dicerio.Engine;

namespace Dicerio.Engine.Tests;

public sealed class ScoringTests
{
    public static IEnumerable<object[]> ValidLockData() => new List<object[]>
    {
        new object[] { new[] { 1 }, 100 },
        new object[] { new[] { 5 }, 50 },
        new object[] { new[] { 1, 5 }, 150 },
        new object[] { new[] { 1, 1 }, 200 },
        new object[] { new[] { 5, 5 }, 100 },
        new object[] { new[] { 1, 1, 1 }, 1000 },
        new object[] { new[] { 2, 2, 2 }, 200 },
        new object[] { new[] { 3, 3, 3 }, 300 },
        new object[] { new[] { 4, 4, 4 }, 400 },
        new object[] { new[] { 5, 5, 5 }, 500 },
        new object[] { new[] { 6, 6, 6 }, 600 },
        new object[] { new[] { 1, 1, 1, 1 }, 2000 },
        new object[] { new[] { 1, 1, 1, 1, 1 }, 4000 },
        new object[] { new[] { 1, 1, 1, 1, 1, 1 }, 8000 },
        new object[] { new[] { 6, 6, 6, 6 }, 1200 },
        new object[] { new[] { 6, 6, 6, 6, 6 }, 2400 },
        new object[] { new[] { 6, 6, 6, 6, 6, 6 }, 4800 },
        new object[] { new[] { 1, 1, 1, 5 }, 1050 },
        new object[] { new[] { 5, 5, 5, 1 }, 600 },
        new object[] { new[] { 1, 2, 3, 4, 5, 6 }, 1500 },
        new object[] { new[] { 6, 5, 4, 3, 2, 1 }, 1500 },
        new object[] { new[] { 3, 3, 3, 1, 5 }, 450 },
        new object[] { new[] { 4, 4, 4, 1, 1 }, 600 },
    };

    [Theory]
    [MemberData(nameof(ValidLockData))]
    public void ScoreLock_returns_expected_points(int[] faces, int expected)
    {
        var result = Scoring.ScoreLock(faces);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Points);
    }

    public static IEnumerable<object[]> InvalidLockData() => new List<object[]>
    {
        new object[] { new[] { 2 } },
        new object[] { new[] { 3 } },
        new object[] { new[] { 4 } },
        new object[] { new[] { 6 } },
        new object[] { new[] { 2, 3 } },
        new object[] { new[] { 1, 2 } },
        new object[] { new[] { 5, 6 } },
        new object[] { new[] { 2, 2 } },
        new object[] { new[] { 6, 6 } },
        new object[] { new[] { 2, 2, 3 } },
        new object[] { new[] { 1, 1, 2 } },
        new object[] { new[] { 1, 2, 3, 4, 5 } },
        new object[] { new[] { 2, 3, 4, 5, 6 } },
        new object[] { new[] { 1, 1, 2, 3, 4, 5 } },
    };

    [Theory]
    [MemberData(nameof(InvalidLockData))]
    public void ScoreLock_rejects_orphan_dice(int[] faces)
    {
        Assert.Null(Scoring.ScoreLock(faces));
    }

    [Fact]
    public void ScoreLock_empty_returns_null()
    {
        Assert.Null(Scoring.ScoreLock(ReadOnlySpan<int>.Empty));
    }

    [Fact]
    public void ScoreLock_throws_on_invalid_face()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scoring.ScoreLock(new[] { 0, 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scoring.ScoreLock(new[] { 7 }));
    }

    [Theory]
    [InlineData(new[] { 2, 3, 4, 6, 6, 2 }, false)]
    [InlineData(new[] { 2, 3, 4, 6, 4, 2 }, false)]
    [InlineData(new[] { 1, 2, 3 }, true)]
    [InlineData(new[] { 5 }, true)]
    [InlineData(new[] { 2, 2, 2 }, true)]
    [InlineData(new[] { 4, 4, 6, 6 }, false)]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6 }, true)]
    [InlineData(new[] { 3, 3, 4, 4, 6, 6 }, false)]
    public void HasAnyScoring_detects_bust(int[] rolled, bool expected)
    {
        Assert.Equal(expected, Scoring.HasAnyScoring(rolled));
    }
}
