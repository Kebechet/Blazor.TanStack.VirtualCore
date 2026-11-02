namespace Kebechet.Blazor.TanStack.VirtualCore;

/// <summary>
/// The .NET half of the slot table the interop module keeps: every distinct (key, size estimate)
/// pair it has been sent, numbered in the order it arrived. An item-set change then travels as slot
/// ids plus only the pairs the JS side does not hold yet, instead of as every key again.
/// </summary>
/// <remarks>
/// <para>
/// The JS side needs the whole set - it answers virtual-core's <c>getItemKey</c> synchronously and
/// carries measurements across a change by key - but what it already holds need not cross again. In
/// a Blazor Hybrid app every interop call is serialized to JSON and copied through the platform's
/// WebView bridge, and a search over a few thousand items used to resend all of them per keystroke.
/// </para>
/// <para>
/// A slot is a key together with its estimate, not the key alone, so an estimate that changes for a
/// key, or two items sharing a key but not an estimate, reach the JS side exactly as before.
/// </para>
/// <para>
/// In <see cref="ItemSetUpdate.Order"/> a non-negative entry is a slot id and a negative entry
/// <c>-n</c> continues the previous slot with the next <c>n</c>, so a stretch of items in the order
/// they were first sent - typically the whole unfiltered list - costs two numbers.
/// </para>
/// </remarks>
internal sealed class ItemSlotTable
{
    /// <summary>
    /// Slots allowed per item of the largest set since the table was last rebuilt. A key that leaves
    /// the set keeps its slot, so a list whose keys keep changing - a live feed - would grow the table
    /// without bound; past this it is rebuilt from the current set, which costs one call carrying
    /// every key, as every call used to.
    /// </summary>
    private const int _maxSlotsPerItem = 2;

    private readonly Dictionary<(string Key, double Size), int> _slotIdsByEntry = [];
    private readonly List<string> _newKeys = [];
    private readonly List<double> _newSizes = [];
    private List<int> _order = [];
    private List<int> _sentOrder = [];
    private int _slotCount;
    private int _largestItemCount;
    private bool _isRebuildRequired;

    /// <summary>
    /// Starts the table over from arrays the JS side takes whole, as it does those the instance is
    /// created from: slot <c>i</c> is item <c>i</c>.
    /// </summary>
    public void Reset(string[] keys, double[] sizes)
    {
        _slotIdsByEntry.Clear();
        for (var index = 0; index < keys.Length; index++)
        {
            _slotIdsByEntry.TryAdd((keys[index], sizes[index]), index);
        }

        _slotCount = keys.Length;
        _largestItemCount = keys.Length;
        _isRebuildRequired = false;
        _sentOrder.Clear();
        if (keys.Length > 0)
        {
            _sentOrder.Add(0);
        }

        if (keys.Length > 1)
        {
            _sentOrder.Add(-(keys.Length - 1));
        }
    }

    /// <summary>
    /// Makes the next <see cref="Encode"/> rebuild the table: a call that failed may or may not have
    /// reached the JS side, so the slots it added cannot be referred to.
    /// </summary>
    public void Invalidate()
    {
        _isRebuildRequired = true;
    }

    /// <summary>The arguments of the interop module's <c>setItems</c> that turn its item set into this one.</summary>
    public ItemSetUpdate Encode(string[] keys, double[] sizes)
    {
        _largestItemCount = Math.Max(_largestItemCount, keys.Length);
        if (!_isRebuildRequired)
        {
            EncodeAgainstTable(keys, sizes);
            if (_slotCount <= _maxSlotsPerItem * _largestItemCount)
            {
                if (_order.SequenceEqual(_sentOrder))
                {
                    return new ItemSetUpdate(null, _newKeys, _newSizes, false);
                }

                (_sentOrder, _order) = (_order, _sentOrder);
                return new ItemSetUpdate(_sentOrder, _newKeys, _newSizes, false);
            }
        }

        Reset(keys, sizes);
        return new ItemSetUpdate(_sentOrder, keys, sizes, true);
    }

    private void EncodeAgainstTable(string[] keys, double[] sizes)
    {
        _order.Clear();
        _newKeys.Clear();
        _newSizes.Clear();
        var runLength = 0;
        var previousSlot = -1;
        for (var index = 0; index < keys.Length; index++)
        {
            var entry = (keys[index], sizes[index]);
            if (!_slotIdsByEntry.TryGetValue(entry, out var slot))
            {
                slot = _slotCount++;
                _slotIdsByEntry.Add(entry, slot);
                _newKeys.Add(keys[index]);
                _newSizes.Add(sizes[index]);
            }

            if (index > 0 && slot == previousSlot + 1)
            {
                runLength++;
            }
            else
            {
                EndRun();
                _order.Add(slot);
            }

            previousSlot = slot;
        }

        EndRun();

        void EndRun()
        {
            if (runLength > 0)
            {
                _order.Add(-runLength);
                runLength = 0;
            }
        }
    }
}

/// <summary>The arguments of the interop module's <c>setItems</c>; see <see cref="ItemSlotTable"/>.</summary>
/// <param name="Order">The set as slot ids, or null when it is the set the JS side already holds.</param>
/// <param name="NewKeys">Keys of the slots appended before <paramref name="Order"/> is read.</param>
/// <param name="NewSizes">Size estimates of those slots.</param>
/// <param name="IsTableReset">Whether the JS side drops its table before appending.</param>
internal readonly record struct ItemSetUpdate(
    IReadOnlyList<int>? Order,
    IReadOnlyList<string> NewKeys,
    IReadOnlyList<double> NewSizes,
    bool IsTableReset);
