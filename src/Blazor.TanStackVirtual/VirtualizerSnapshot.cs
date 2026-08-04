namespace Kebechet.Blazor.TanStackVirtual;

/// <summary>
/// A read of the virtualizer's live scroll state. Deserialized from JS interop, so the property
/// names are a wire contract with <c>adaptive-virtualize-interop.js</c>.
/// </summary>
public sealed class VirtualizerSnapshot
{
    /// <summary>Current scroll offset along the scroll axis, in pixels.</summary>
    public double ScrollOffset { get; set; }

    /// <summary>Total scrollable size in pixels.</summary>
    public double TotalSize { get; set; }

    /// <summary>True while a scroll is in progress.</summary>
    public bool IsScrolling { get; set; }

    /// <summary>True once the end of the list has been reached.</summary>
    public bool IsAtEnd { get; set; }

    /// <summary>
    /// Which way the list is scrolling: <c>forward</c>, <c>backward</c>, or null when at rest.
    /// </summary>
    public string? ScrollDirection { get; set; }

    /// <summary>First item index in the rendered window, or -1 when nothing is rendered.</summary>
    public int StartIndex { get; set; }

    /// <summary>Last item index in the rendered window, or -1 when nothing is rendered.</summary>
    public int EndIndex { get; set; }
}
