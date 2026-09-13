namespace Ai4Sts2.Core;

public sealed class Zobrist
{
    private readonly ulong[] _keys;

    public Zobrist(int slots, int valuesPerSlot, ulong seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valuesPerSlot);
        ValuesPerSlot = valuesPerSlot;
        _keys = new ulong[checked(slots * valuesPerSlot)];
        var state = seed;
        for (var i = 0; i < _keys.Length; i++)
        {
            _keys[i] = SplitMix64.Next(ref state);
        }
    }

    public int Slots => _keys.Length / ValuesPerSlot;

    public int ValuesPerSlot { get; }

    public ulong Key(int slot, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Slots);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, ValuesPerSlot);
        return _keys[(slot * ValuesPerSlot) + value];
    }

    public ulong Replace(ulong hash, int slot, int oldValue, int newValue) =>
        hash ^ Key(slot, oldValue) ^ Key(slot, newValue);
}
