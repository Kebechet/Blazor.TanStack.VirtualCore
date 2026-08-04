namespace Kebechet.Blazor.TanStack.VirtualCore;

/// <summary>
/// One row's computed placement, as reported by the virtualizer. Deserialized from JS interop, so
/// the property names are a wire contract with <c>adaptive-virtualize-interop.js</c>.
/// </summary>
public sealed class VirtualItem
{
    /// <summary>Index into the current item set.</summary>
    public int Index { get; set; }

    /// <summary>Pixel offset of the row's top edge from the start of the scrollable content.</summary>
    public double Start { get; set; }

    /// <summary>Measured pixel height, or the estimate if this row has not been measured yet.</summary>
    public double Size { get; set; }

    /// <summary>Pixel offset of the row's bottom edge.</summary>
    public double End { get; set; }

    /// <summary>
    /// The row's stable key. Compared against the freshly extracted key at render time - a mismatch
    /// means the item set moved under this placement and the offsets are stale.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Which lane the item landed in, zero-based. Always 0 unless <c>Lanes</c> is greater than one;
    /// the component turns it into the item's cross-axis position.
    /// </summary>
    public int Lane { get; set; }
}
