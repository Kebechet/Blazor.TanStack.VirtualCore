using System.Text.Json.Serialization;

namespace Kebechet.Blazor.TanStack.VirtualCore;

/// <summary>
/// Every <c>VirtualizerOptions</c> member that a consumer can meaningfully set. Each is nullable:
/// leaving one unset is the only signal for "keep virtual-core's own default", so the wrapper never
/// invents a default upstream does not have.
/// </summary>
/// <remarks>
/// The members virtual-core requires as functions - <c>count</c>, <c>estimateSize</c>,
/// <c>getItemKey</c>, <c>getScrollElement</c>, <c>observeElementRect</c>,
/// <c>observeElementOffset</c>, <c>scrollToFn</c>, <c>measureElement</c> and <c>onChange</c> - are
/// supplied by the component itself from <c>Items</c>, <c>ItemKeyExtractor</c>,
/// <c>ItemSizeEstimator</c> and <see cref="UseWindowScroller"/>. Setting them from .NET would mean a
/// JS interop round-trip per scroll frame.
/// </remarks>
public sealed class VirtualizeOptions
{
    /// <summary>Extra items rendered beyond each edge of the viewport to mask scroll latency.</summary>
    [JsonPropertyName("overscan")]
    public int? Overscan { get; set; }

    /// <summary>
    /// Items kept rendered ahead of the viewport, on the side the user is scrolling towards. Leave
    /// unset to keep <see cref="Overscan"/> on that side.
    /// </summary>
    /// <remarks>
    /// Buffering further along the direction of travel than behind it is what react-window,
    /// react-virtualized and Angular's CDK all do by default: that is where the next items are
    /// actually needed, so the same budget masks more scroll latency.
    /// <para>
    /// The side this lands on follows the virtualizer's scroll direction and flips when the user
    /// reverses. A list that has never scrolled reports no direction and is treated as scrolling
    /// forward.
    /// </para>
    /// </remarks>
    [JsonPropertyName("overscanLeading")]
    public int? OverscanLeading { get; set; }

    /// <summary>
    /// Items kept rendered behind the direction of travel - the floor a reversal starts from. Leave
    /// unset to keep <see cref="Overscan"/> on that side.
    /// </summary>
    /// <remarks>
    /// Unlike react-window's equivalent, this is held at all times rather than lapsing when the
    /// scroll stops: virtual-core memoizes its rendered range on
    /// <c>[rangeExtractor, overscan, count, startIndex, endIndex]</c>, which excludes
    /// <c>isScrolling</c>, so a value that lapsed would simply never be recomputed and the bias would
    /// outlive the scroll instead.
    /// <para>
    /// Holding it means reversing is never worse than a symmetric window of the same size, so leave
    /// it at whatever <see cref="Overscan"/> you would otherwise have used and treat
    /// <see cref="OverscanLeading"/> as the addition. Raise it further when unmounting an item is
    /// expensive - a row owning a media element, say - since those items are already mounted and
    /// holding them costs far less than rebuilding them.
    /// </para>
    /// </remarks>
    [JsonPropertyName("overscanTrailing")]
    public int? OverscanTrailing { get; set; }

    /// <summary>Lays the list out along the x axis instead of the y axis.</summary>
    [JsonPropertyName("horizontal")]
    public bool? IsHorizontal { get; set; }

    /// <summary>
    /// Number of parallel lanes - a masonry or multi-column grid. Items are distributed across lanes
    /// and each <c>VirtualItem</c> reports the <c>Lane</c> it landed in.
    /// </summary>
    [JsonPropertyName("lanes")]
    public int? Lanes { get; set; }

    /// <summary>
    /// Whether lanes are balanced using each item's estimate or its measured size. <c>Measured</c>
    /// packs more evenly but only settles once measurement has caught up.
    /// </summary>
    [JsonPropertyName("laneAssignmentMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LaneAssignmentMode? LaneAssignmentMode { get; set; }

    /// <summary>Space inserted between consecutive items, in pixels.</summary>
    [JsonPropertyName("gap")]
    public double? Gap { get; set; }

    /// <summary>Padding before the first item, in pixels.</summary>
    [JsonPropertyName("paddingStart")]
    public double? PaddingStart { get; set; }

    /// <summary>Padding after the last item, in pixels.</summary>
    [JsonPropertyName("paddingEnd")]
    public double? PaddingEnd { get; set; }

    /// <summary>Offset kept between the viewport edge and an item scrolled to with <c>start</c> alignment.</summary>
    [JsonPropertyName("scrollPaddingStart")]
    public double? ScrollPaddingStart { get; set; }

    /// <summary>Offset kept between the viewport edge and an item scrolled to with <c>end</c> alignment.</summary>
    [JsonPropertyName("scrollPaddingEnd")]
    public double? ScrollPaddingEnd { get; set; }

    /// <summary>
    /// Distance between the scroll container's start and the virtualized region. Set this when other
    /// content sits above the list inside the same scroller, or every offset is shifted by that much.
    /// </summary>
    [JsonPropertyName("scrollMargin")]
    public double? ScrollMargin { get; set; }

    /// <summary>Scroll offset applied before the first measurement, for restoring a saved position.</summary>
    [JsonPropertyName("initialOffset")]
    public double? InitialOffset { get; set; }

    /// <summary>
    /// Viewport size assumed before the scroll element has been measured. Supplying it avoids a
    /// first paint at zero size during server prerendering.
    /// </summary>
    [JsonPropertyName("initialRect")]
    public VirtualizeRect? InitialRect { get; set; }

    /// <summary>
    /// Measurements to seed the cache with, from a previous <c>TakeSnapshotAsync</c>. Restores exact
    /// scroll geometry across a navigation without re-measuring every row.
    /// </summary>
    [JsonPropertyName("initialMeasurementsCache")]
    public VirtualItem[]? InitialMeasurementsCache { get; set; }

    /// <summary>Which edge the list stays anchored to when items are inserted or resized.</summary>
    [JsonPropertyName("anchorTo")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScrollAnchor? AnchorTo { get; set; }

    /// <summary>
    /// Whether the viewport follows newly appended items, and how. <c>None</c> stays put;
    /// the others scroll with the matching behaviour. Useful for a log or chat transcript.
    /// </summary>
    [JsonPropertyName("followOnAppend")]
    public FollowOnAppend? FollowOnAppend { get; set; }

    /// <summary>Milliseconds of quiet before a scroll is treated as finished.</summary>
    [JsonPropertyName("scrollEndThreshold")]
    public int? ScrollEndThreshold { get; set; }

    /// <summary>Milliseconds before <c>isScrolling</c> resets after the last scroll event.</summary>
    [JsonPropertyName("isScrollingResetDelay")]
    public int? ScrollingResetDelay { get; set; }

    /// <summary>
    /// Uses the native <c>scrollend</c> event instead of a timer to detect the end of a scroll.
    /// More accurate where supported; falls back automatically where it is not.
    /// </summary>
    [JsonPropertyName("useScrollendEvent")]
    public bool? UseScrollEndEvent { get; set; }

    /// <summary>Right-to-left layout, which flips the direction offsets are measured in.</summary>
    [JsonPropertyName("isRtl")]
    public bool? IsRtl { get; set; }

    /// <summary>
    /// While false, no measuring or scroll observation happens, and upstream <b>resets</b> the
    /// virtualizer: it empties the
    /// rendered window and clears its measurement caches, so every item unmounts - a reset, not a
    /// freeze. Setting it back to true re-observes, re-measures and re-renders at the preserved
    /// scroll position.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? IsEnabled { get; set; }

    /// <summary>Defers ResizeObserver callbacks to an animation frame, smoothing bursts of resizes.</summary>
    [JsonPropertyName("useAnimationFrameWithResizeObserver")]
    public bool? UseAnimationFrameWithResizeObserver { get; set; }

    /// <summary>Reuses cached measurements across an item-set change rather than re-measuring.</summary>
    [JsonPropertyName("useCachedMeasurements")]
    public bool? UseCachedMeasurements { get; set; }

    /// <summary>
    /// Attribute the virtualizer reads an element's index from. The component renders
    /// <c>data-index</c>, so change this only if you also override the rendered markup.
    /// </summary>
    [JsonPropertyName("indexAttribute")]
    public string? IndexAttribute { get; set; }

    /// <summary>Logs virtual-core's internal decisions to the browser console.</summary>
    [JsonPropertyName("debug")]
    public bool? IsDebugEnabled { get; set; }

    /// <summary>
    /// Indices always kept in the rendered window, however far off-screen they are - sticky headers,
    /// a pinned selection, a row being dragged.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>rangeExtractor</c> is a JavaScript function invoked on every range computation.
    /// Marshalling that to .NET would mean an interop round-trip per scroll frame, so it is modelled
    /// as this declarative list, which the interop layer compiles into a <c>rangeExtractor</c> that
    /// unions these indices with virtual-core's own default range.
    /// </remarks>
    [JsonPropertyName("alwaysRenderedIndexes")]
    public int[]? AlwaysRenderedIndexes { get; set; }

    /// <summary>
    /// Virtualizes against the window rather than a scrollable ancestor, using virtual-core's
    /// <c>observeWindowRect</c> / <c>observeWindowOffset</c> / <c>windowScroll</c> trio. Use it for a
    /// list that scrolls with the whole document rather than inside its own box.
    /// </summary>
    [JsonPropertyName("useWindowScroller")]
    public bool? UseWindowScroller { get; set; }
}

/// <summary>A viewport size, matching virtual-core's <c>Rect</c>.</summary>
public sealed class VirtualizeRect
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

/// <summary>Which edge the list stays anchored to when content shifts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScrollAnchor
{
    /// <summary>Keeps the top (or left) edge stable.</summary>
    Start,
    /// <summary>Keeps the bottom (or right) edge stable - the usual choice for a chat transcript.</summary>
    End
}

/// <summary>How lanes are balanced when <c>Lanes</c> is greater than one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LaneAssignmentMode
{
    /// <summary>Balances using each item's estimate. Stable from first paint, less even.</summary>
    Estimate,
    /// <summary>Balances using measured sizes. Packs more evenly once measurement settles.</summary>
    Measured
}

/// <summary>
/// Whether and how the viewport follows items appended to the end of the list.
/// </summary>
/// <remarks>
/// Upstream types this as <c>boolean | ScrollBehavior</c>. The interop layer maps
/// <see cref="FollowOnAppend.None"/> to <c>false</c> and the rest to their behaviour string, so the
/// union is expressed here as one enum rather than leaking a two-typed property.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FollowOnAppend
{
    /// <summary>Stays where the user left it.</summary>
    None,
    /// <summary>Follows, letting the browser choose the animation.</summary>
    Auto,
    /// <summary>Follows with a smooth animation.</summary>
    Smooth,
    /// <summary>Follows immediately, with no animation.</summary>
    Instant
}
