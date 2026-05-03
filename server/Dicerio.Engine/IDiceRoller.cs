using System.Security.Cryptography;

namespace Dicerio.Engine;

public interface IDiceRoller
{
    /// <summary>Inclusive range [1,6] roll.</summary>
    int Roll();

    /// <summary>Pick a random integer in [0, exclusiveMax). Used for opening seat assignment.</summary>
    int Next(int exclusiveMax);
}

public sealed class CryptoDiceRoller : IDiceRoller
{
    public int Roll() => RandomNumberGenerator.GetInt32(1, 7);

    public int Next(int exclusiveMax) => RandomNumberGenerator.GetInt32(0, exclusiveMax);
}
