using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kebechet.Blazor.TanStack.VirtualCore;

/// <summary>
/// Virtualization that delegates scroll math and measurement to <c>@tanstack/virtual-core</c>, while
/// Blazor keeps ownership of rendering. The JS side reports which item indices are visible and at
/// what offsets; this component renders that slice with absolute positioning inside a container
/// sized to the virtualizer's reported total. After every render the visible items are re-measured
/// so the virtualizer can refine its cache.
/// </summary>
/// <remarks>
/// Unlike the framework's <c>Virtualize</c>, every item is measured individually rather than
/// approximated from a single <c>ItemSize</c> or a running average, so items of genuinely different
/// sizes land at the correct offset instead of drifting. Vertical, horizontal and multi-lane
/// (masonry) layouts are all supported through <see cref="Options"/>.
/// </remarks>
/// <typeparam name="TItem">Item data type.</typeparam>
public partial class AdaptiveVirtualize<TItem> : IAsyncDisposable
{
    /// <summary>Items to virtualize. Changing the reference re-seeds the virtualizer.</summary>
    [Parameter][EditorRequired] public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>Renders a single item.</summary>
    [Parameter][EditorRequired] public RenderFragment<TItem> ItemContent { get; set; } = default!;

    /// <summary>
    /// Stable identity for an item, used both as Blazor's <c>@key</c> and as the virtualizer's
    /// measurement-cache key. Two different items must never produce the same string, and the same
    /// item must produce the same string across renders, or measurements attach to the wrong item.
    /// </summary>
    [Parameter][EditorRequired] public Func<TItem, string> ItemKeyExtractor { get; set; } = default!;

    /// <summary>
    /// Optional per-item size estimator returning the expected pixel size along the scroll axis,
    /// used before the item has been measured. An estimate within ~20% removes first-render flicker
    /// because the item lands at roughly the right offset before measurement refines it. When null,
    /// every item uses <see cref="EstimatedItemSize"/>.
    /// </summary>
    [Parameter] public Func<TItem, double>? ItemSizeEstimator { get; set; }

    /// <summary>
    /// Fallback pixel estimate when <see cref="ItemSizeEstimator"/> is null. Prefer the per-item
    /// estimator when sizes vary by shape - a single value for mixed-size items is what produces the
    /// visible snap as measurements arrive.
    /// </summary>
    [Parameter] public double EstimatedItemSize { get; set; } = 50;

    /// <summary>
    /// The upstream <c>VirtualizerOptions</c> surface. Changing the reference pushes the new values
    /// to the live virtualizer.
    /// </summary>
    [Parameter] public VirtualizeOptions? Options { get; set; }

    /// <summary>
    /// Fires after each measurement pass completes. Use it as a "the DOM for the current item set
    /// has been measured" signal - for example to drive a two-phase render where extra content is
    /// appended only once the preceding item's real size is known, so the appended items land at the
    /// right offset on first paint instead of estimating and snapping.
    /// </summary>
    /// <remarks>
    /// Deliberately a <see cref="Func{TResult}"/> rather than an <c>EventCallback</c>: the latter
    /// calls <c>StateHasChanged</c> on the receiver after the handler returns, which on a page with
    /// a debounced search input re-renders the bound input with a stale value and clobbers
    /// in-flight keystrokes. A plain delegate fires without the implicit render; handlers that need
    /// one can call <c>StateHasChanged</c> themselves.
    /// </remarks>
    [Parameter] public Func<Task>? OnLayoutMeasured { get; set; }

    private const string _modulePath = "./_content/Kebechet.Blazor.TanStack.VirtualCore/adaptive-virtualize-interop.js";

    private ElementReference _rootElement;
    private DotNetObjectReference<AdaptiveVirtualize<TItem>>? _selfRef;
    private IJSObjectReference? _jsModule;
    private IJSObjectReference? _jsInstance;

    private IReadOnlyList<VirtualItem> _virtualItems = [];
    private double _totalSize;
    private IReadOnlyList<TItem>? _lastItemsRef;
    private VirtualizeOptions? _lastOptionsRef;

    private int _lastMeasuredFirstIndex = -1;
    private int _lastMeasuredLastIndex = -1;

    /// <summary>
    /// Reusable interop buffers. Rebuilding these is O(items), so for a large set it is worth not
    /// reallocating them on every item-set change.
    /// </summary>
    private string[] _itemKeys = [];
    private double[] _itemSizes = [];

    private readonly List<PreparedRow> _preparedRows = [];
    private IReadOnlyList<VirtualItem>? _preparedFromVirtualItems;
    private IReadOnlyList<TItem>? _preparedFromItems;
    private VirtualizeOptions? _preparedFromOptions;

    private bool _isHorizontal => Options?.IsHorizontal == true;
    private int _laneCount => Options?.Lanes is > 1 ? Options.Lanes.Value : 1;

    private string _containerStyle
    {
        get
        {
            var size = $"{ToCss(_totalSize)}px";
            return _isHorizontal
                ? $"position:relative;height:100%;width:{size};"
                : $"position:relative;width:100%;height:{size};";
        }
    }

    /// <summary>
    /// Places one item. The scroll axis carries the virtualizer's offset; the cross axis is split
    /// evenly between lanes, which is what turns <c>Lanes</c> into an actual masonry grid rather
    /// than a stack of overlapping items.
    /// </summary>
    /// <remarks>
    /// Called once per rendered row per window change - never per render - because the results are
    /// held in <see cref="_preparedRows"/>. Building them inline in the markup meant a handful of
    /// string allocations per row on every single render batch, and a busy list produces hundreds of
    /// those per scroll.
    /// </remarks>
    private string GetItemStyle(VirtualItem virtualItem)
    {
        var lanes = _laneCount;
        // A single-lane list is the overwhelmingly common case, and there the cross axis is simply
        // the full width - worth not formatting two constants per row to say so.
        var crossSize = lanes == 1 ? "100" : ToCss(100d / lanes);
        var crossStart = lanes == 1 ? "0" : ToCss(virtualItem.Lane * (100d / lanes));

        if (_isHorizontal)
        {
            return $"position:absolute;left:0;top:{crossStart}%;height:{crossSize}%;" +
                $"transform:translateX({ToCss(virtualItem.Start)}px);";
        }

        return $"position:absolute;top:0;left:{crossStart}%;width:{crossSize}%;" +
            $"transform:translateY({ToCss(virtualItem.Start)}px);";
    }

    /// <summary>
    /// The rows to render, with their key and placement resolved once per change of the window, the
    /// item set or the options - rather than on every render, which is where they used to be built.
    /// </summary>
    private List<PreparedRow> _rowsToRender
    {
        get
        {
            if (ReferenceEquals(_preparedFromVirtualItems, _virtualItems) &&
                ReferenceEquals(_preparedFromItems, Items) &&
                ReferenceEquals(_preparedFromOptions, Options))
            {
                return _preparedRows;
            }

            _preparedFromVirtualItems = _virtualItems;
            _preparedFromItems = Items;
            _preparedFromOptions = Options;
            _preparedRows.Clear();

            foreach (var virtualItem in _virtualItems)
            {
                if (virtualItem.Index < 0 || virtualItem.Index >= Items.Count)
                {
                    continue;
                }

                var item = Items[virtualItem.Index];
                var key = ItemKeyExtractor(item);

                // If the row at this index has changed since the virtualizer emitted this virtualItem
                // (e.g. a group expanded and inserted children), the virtualItem's start/size is stale
                // for the new content. Rendering with the stale offset visibly misplaces the row until
                // the next JS round-trip arrives with fresh positions - on slow JS interop (Android
                // WebView) that gap is hundreds of ms, long enough to see rows land where other rows
                // used to be. Skip the mismatched row so unchanged rows stay visible and changed rows
                // go blank until JS catches up.
                if (key != virtualItem.Key)
                {
                    continue;
                }

                _preparedRows.Add(new PreparedRow(key, virtualItem.Index, GetItemStyle(virtualItem), item));
            }

            return _preparedRows;
        }
    }

    /// <remarks>
    /// Plain constructor rather than <c>required</c> members: the package targets down to net6.0,
    /// where <c>RequiredMemberAttribute</c> does not exist.
    /// </remarks>
    private sealed class PreparedRow
    {
        public PreparedRow(string key, int index, string style, TItem item)
        {
            Key = key;
            Index = index;
            Style = style;
            Item = item;
        }

        public string Key { get; }
        public int Index { get; }
        public string Style { get; }
        public TItem Item { get; }
    }

    /// <summary>
    /// Formats invariantly. A comma decimal separator (cs-CZ, sk-SK, de-DE, ...) produces
    /// <c>translateY(12,5px)</c>, which the browser drops as an invalid declaration - the item then
    /// renders at offset zero and every item stacks on the first.
    /// </summary>
    private static string ToCss(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            _jsModule = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", _modulePath);
            BuildDescriptors();
            _jsInstance = await _jsModule.InvokeAsync<IJSObjectReference>(
                "create", _rootElement, _selfRef, _itemKeys, _itemSizes, EstimatedItemSize, Options);
            _lastItemsRef = Items;
            _lastOptionsRef = Options;

            // The JS constructor notifies synchronously, so the first OnVirtualItemsChanged - and
            // the render it queues - can complete before the await above has assigned _jsInstance.
            // That render reaches OnAfterRenderAsync, finds _jsInstance still null, and returns
            // without measuring. On a page that never renders again for any other reason, nothing
            // re-triggers the measure path and every item keeps its estimated size forever: large
            // items overlap their neighbours and the list never converges.
            // Requesting one more render here guarantees a pass that runs with _jsInstance set.
            StateHasChanged();
            return;
        }

        if (_jsInstance is null)
        {
            return;
        }

        if (!ReferenceEquals(_lastOptionsRef, Options))
        {
            _lastOptionsRef = Options;
            await _jsInstance.InvokeVoidAsync("setOptions", Options);
        }

        var hasItemsChanged = !ReferenceEquals(_lastItemsRef, Items);
        if (hasItemsChanged)
        {
            // Assign BEFORE awaiting. The await yields, during which the virtualizer's measure()
            // fires onChange -> OnVirtualItemsChanged -> StateHasChanged, queuing another render
            // that re-enters OnAfterRenderAsync before this call returns. Without the early
            // assignment that re-entry still sees the old reference and fires a second, redundant
            // setItems.
            _lastItemsRef = Items;
            BuildDescriptors();
            await _jsInstance.InvokeVoidAsync("setItems", _itemKeys, _itemSizes);
        }

        var firstIndex = _virtualItems.Count > 0
            ? _virtualItems[0].Index
            : -1;
        var lastIndex = _virtualItems.Count > 0
            ? _virtualItems[_virtualItems.Count - 1].Index
            : -1;

        // Only re-measure when the rendered window actually changed. Unrelated re-renders (a
        // selection toggle, a state pulse) bring no new DOM into view, and a measure pass there is a
        // querySelectorAll plus a forced layout per item plus an interop round-trip, for nothing.
        // An items change always forces one because the keys behind those indices may differ.
        //
        // Deliberately NOT narrowed to "some rendered key has never been measured", which would also
        // skip the pass when scrolling back over rows seen earlier. A row's height can change without
        // its key changing and without InvalidateItemSize being called - content arriving late, a
        // child rendering, an animation settling - and this pass is what catches that. Pinned by
        // ExpandingAGroup_InsertsChildrenWithoutOverlappingTheGroupRow, which fails on exactly that.
        if (!hasItemsChanged &&
            firstIndex == _lastMeasuredFirstIndex &&
            lastIndex == _lastMeasuredLastIndex)
        {
            return;
        }

        // Same pre-await race as setItems: measuring notifies, which renders, which re-enters here.
        _lastMeasuredFirstIndex = firstIndex;
        _lastMeasuredLastIndex = lastIndex;

        await _jsInstance.InvokeVoidAsync("measureRenderedRows");

        if (OnLayoutMeasured is not null)
        {
            await OnLayoutMeasured.Invoke();
        }
    }

    /// <summary>
    /// Forgets the cached size for one item key. Use when an item's content changed shape without
    /// its key changing - a group row toggling between collapsed and expanded is the usual case. The
    /// next render falls back to the estimate for that key, then measurement refines it.
    /// </summary>
    /// <remarks>Has no direct upstream equivalent; it evicts one key and bumps the cache version.</remarks>
    public async Task InvalidateItemSize(string key)
    {
        await InvokeInstance("invalidateItemSize", key);
    }

    /// <summary>Scrolls so the item at <paramref name="index"/> is positioned per <paramref name="align"/>.</summary>
    public async Task ScrollToIndex(int index, ScrollAlignment align = ScrollAlignment.Start, ScrollBehavior behavior = ScrollBehavior.Auto)
    {
        await InvokeInstance("scrollToIndex", index, align.ToJsName(), behavior.ToJsName());
    }

    /// <summary>Scrolls to an absolute pixel offset along the scroll axis.</summary>
    public async Task ScrollToOffset(double offset, ScrollAlignment align = ScrollAlignment.Start, ScrollBehavior behavior = ScrollBehavior.Auto)
    {
        await InvokeInstance("scrollToOffset", offset, align.ToJsName(), behavior.ToJsName());
    }

    /// <summary>Scrolls to the end of the list.</summary>
    public async Task ScrollToEnd(ScrollBehavior behavior = ScrollBehavior.Auto)
    {
        await InvokeInstance("scrollToEnd", behavior.ToJsName());
    }

    /// <summary>Scrolls by a relative pixel delta.</summary>
    public async Task ScrollBy(double delta, ScrollBehavior behavior = ScrollBehavior.Auto)
    {
        await InvokeInstance("scrollBy", delta, behavior.ToJsName());
    }

    /// <summary>
    /// Jumps to the start of the list. Call whenever the item set changes in a way that makes the
    /// previous offset meaningless, such as a new search query.
    /// </summary>
    public async Task ScrollToTop()
    {
        await ScrollToIndex(0, ScrollAlignment.Start, ScrollBehavior.Instant);
    }

    /// <summary>
    /// Overrides an item's measured size directly, without waiting for the DOM to be measured.
    /// Use when the new size is already known - a drag handle resizing a row, for instance.
    /// </summary>
    public async Task ResizeItem(int index, double size)
    {
        await InvokeInstance("resizeItem", index, size);
    }

    /// <summary>Discards every cached measurement and recomputes from the estimates.</summary>
    public async Task Measure()
    {
        await InvokeInstance("measure");
    }

    /// <summary>Total scrollable size in pixels, as the virtualizer currently computes it.</summary>
    public async Task<double> GetTotalSize()
    {
        return await QueryInstance<double>("getTotalSize");
    }

    /// <summary>The item indices currently in the rendered window, including overscan.</summary>
    public async Task<int[]> GetVirtualIndexes()
    {
        return await QueryInstance<int[]>("getVirtualIndexes") ?? [];
    }

    /// <summary>The scroll offset that would place <paramref name="index"/> per <paramref name="align"/>.</summary>
    public async Task<double> GetOffsetForIndex(int index, ScrollAlignment align = ScrollAlignment.Start)
    {
        return await QueryInstance<double>("getOffsetForIndex", index, align.ToJsName());
    }

    /// <summary>The item occupying a given scroll offset, or null when the offset is past the end.</summary>
    public async Task<VirtualItem?> GetVirtualItemForOffset(double offset)
    {
        return await QueryInstance<VirtualItem?>("getVirtualItemForOffset", offset);
    }

    /// <summary>Pixels remaining between the current scroll position and the end of the list.</summary>
    public async Task<double> GetDistanceFromEnd()
    {
        return await QueryInstance<double>("getDistanceFromEnd");
    }

    /// <summary>Live scroll state: offset, direction, whether a scroll is in progress, and the rendered range.</summary>
    public async Task<VirtualizerSnapshot?> GetSnapshot()
    {
        return await QueryInstance<VirtualizerSnapshot?>("getSnapshot");
    }

    /// <summary>
    /// The virtualizer's own measurement snapshot, suitable for
    /// <see cref="VirtualizeOptions.InitialMeasurementsCache"/> on a later mount - restoring exact
    /// geometry across a navigation without re-measuring.
    /// </summary>
    public async Task<VirtualItem[]> TakeSnapshot()
    {
        return await QueryInstance<VirtualItem[]>("takeSnapshot") ?? [];
    }

    private async Task InvokeInstance(string method, params object?[] args)
    {
        if (_jsInstance is null)
        {
            return;
        }

        await _jsInstance.InvokeVoidAsync(method, args);
    }

    private async Task<TResult?> QueryInstance<TResult>(string method, params object?[] args)
    {
        if (_jsInstance is null)
        {
            return default;
        }

        return await _jsInstance.InvokeAsync<TResult>(method, args);
    }

    /// <summary>
    /// Snapshots <see cref="Items"/> into the two arrays the JS side needs to answer the
    /// virtualizer's <c>getItemKey</c> and <c>estimateSize</c> callbacks synchronously. Sent once
    /// per <see cref="Items"/> reference change - never on scroll.
    /// </summary>
    /// <remarks>
    /// Two parallel arrays rather than one array of objects, because this crosses the interop
    /// boundary as JSON and covers the <b>whole</b> item set, not just the rendered window. For a
    /// few thousand items the object form meant that many heap allocations plus a payload several
    /// times larger, rebuilt on every filter keystroke. The buffers are reused for the same reason.
    /// </remarks>
    private void BuildDescriptors()
    {
        if (_itemKeys.Length != Items.Count)
        {
            _itemKeys = new string[Items.Count];
            _itemSizes = new double[Items.Count];
        }

        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];
            _itemKeys[index] = ItemKeyExtractor(item);
            _itemSizes[index] = ItemSizeEstimator?.Invoke(item) ?? EstimatedItemSize;
        }
    }

    [JSInvokable]
    public void OnVirtualItemsChanged(VirtualItem[] virtualItems, double totalSize)
    {
        _virtualItems = virtualItems;
        _totalSize = totalSize;
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_jsInstance is not null)
        {
            try
            {
                await _jsInstance.InvokeVoidAsync("dispose");
                await _jsInstance.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit or WebView already torn down - the JS instance went with it.
            }
            catch (ObjectDisposedException)
            {
                // JS runtime already disposed during app shutdown.
            }
            catch (JSException)
            {
                // The page no longer holds this object ("JS object instance with ID n does not
                // exist"). When a BlazorWebView's page reloads, it is the reloaded page's attach
                // request that disposes the previous page's renderer, so the dispose call reaches a
                // page that never created the object. Blazor reports that as a JSException, not as
                // a disconnect.
            }

            _jsInstance = null;
        }

        if (_jsModule is not null)
        {
            try
            {
                await _jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _jsModule = null;
        }

        _selfRef?.Dispose();
        _selfRef = null;
    }
}
