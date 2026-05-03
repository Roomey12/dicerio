using Dicerio.Engine;

namespace Dicerio.Engine.Tests;

public sealed class ScriptedRoller : IDiceRoller
{
    private readonly Queue<int> _rolls;
    private readonly Queue<int> _ints;

    public ScriptedRoller(IEnumerable<int> rolls, IEnumerable<int>? ints = null)
    {
        _rolls = new Queue<int>(rolls);
        _ints = new Queue<int>(ints ?? new[] { 0 });
    }

    public int Roll() => _rolls.Dequeue();

    public int Next(int exclusiveMax) => _ints.Count == 0 ? 0 : _ints.Dequeue();

    public int Remaining => _rolls.Count;
}
