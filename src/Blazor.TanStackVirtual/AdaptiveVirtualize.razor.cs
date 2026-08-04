using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kebechet.Blazor.TanStackVirtual;

/// <summary>
/// Variable-height virtualization that delegates scroll math and measurement to
/// <c>@tanstack/virtual-core</c>, while Blazor keeps ownership of rendering. The JS side reports
/// which item indices are visible and at what offsets; this component renders that slice with
/// absolute positioning inside a container sized to the virtualizer's reported total. After every
/// render the visible rows are re-measured so the virtualizer can refine its cache.
/// </summary>
/// <remarks>
/// Unlike the framework's <c>Virtualize</c>, every row is measured individually rather than
/// approximated from a single <c>ItemSize</c> or a running average, so rows of genuinely different
/// heights land at the correct offset instead of drifting.
/// </remarks>
/// <typeparam name="TItem">Row data type.</typeparam>
public partial class AdaptiveVirtualize<TItem> : IAsyncDisposable
{
    /// <summary>Rows to virtualize. Changing the reference re-seeds the virtualizer.</summary>
    [Parameter][EditorRequired] public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>Renders a single row.</summary>
    [Parameter][EditorRequired] public RenderFragment<TItem> ItemContent { get; set; } = default!;

    /// <summary>
    /// Stable identity for a row, used both as Blazor's <c>@key</c> and as the virtualizer's
    /// measurement-cache key. Two different rows must never produce the same string, and the same
    /// row must produce the same string across renders, or measurements attach to the wrong row.
    /// </summary>
    [Parameter][EditorRequired] public Func<TItem, string> ItemKeyExtractor { get; set; } = default!;

    /// <summary>
    /// Optional per-row size estimator returning the expected pixel height, used before the row has
    /// been measured. An estimate within ~20% removes first-render flicker because the row lands at
    /// roughly the right offset before measurement refines it. When null, every row uses
    /// <see cref="EstimatedItemSize"/>.
    /// </summary>
    [Parameter] public Func<TItem, double>? ItemSizeEstimator { get; set; }

    /// <summary>
    /// Fallback pixel estimate when <see cref="ItemSizeEstimator"/> is null. Prefer the per-row
    /// estimator when row heights vary by shape - a single value for mixed-height rows is what
    /// produces the visible snap as measurements arrive.
    /// </summary>
    [Parameter] public double EstimatedItemSize { get; set; } = 50;

    /// <summary>Extra rows rendered above and below the viewport to mask scroll latency.</summary>
    [Parameter] public int Overscan { get; set; } = 5;

    /// <summary>
    /// Fires after each measurement pass completes. Use it as a "the DOM for the current item set
    /// has been measured" signal - for example to drive a two-phase render where extra content is
    /// appended only once the preceding row's real size is known, so the appended rows land at the
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

    private const string _modulePath = "./_content/Kebechet.Blazor.TanStackVirtual/adaptive-virtualize-interop.js";

    private ElementReference _rootElement;
    private DotNetObjectReference<AdaptiveVirtualize<TItem>>? _selfRef;
    private IJSObjectReference? _jsModule;
    private IJSObjectReference? _jsInstance;

    private IReadOnlyList<VirtualItem> _virtualItems = [];
    private double _totalSize;
    private IReadOnlyList<TItem>? _lastItemsRef;
    private int _lastMeasuredFirstIndex = -1;
    private int _lastMeasuredLastIndex = -1;

    private string _containerStyle => $"position:relative;width:100%;height:{ToCss(_totalSize)}px;";

    private static string GetItemStyle(VirtualItem virtualItem)
    {
        return $"position:absolute;top:0;left:0;width:100%;transform:translateY({ToCss(virtualItem.Start)}px);";
    }

    /// <summary>
    /// Formats invariantly. A comma decimal separator (cs-CZ, sk-SK, de-DE, ...) produces
    /// <c>translateY(12,5px)</c>, which the browser drops as an invalid declaration - the row then
    /// renders at offset zero and every row stacks on the first.
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
            _jsInstance = await _jsModule.InvokeAsync<IJSObjectReference>(
                "create", _rootElement, _selfRef, BuildDescriptors(), Overscan, EstimatedItemSize);
            _lastItemsRef = Items;

            // The JS constructor notifies synchronously, so the first OnVirtualItemsChanged - and
            // the render it queues - can complete before the await above has assigned _jsInstance.
            // That render reaches OnAfterRenderAsync, finds _jsInstance still null, and returns
            // without measuring. On a page that never renders again for any other reason, nothing
            // re-triggers the measure path and every row keeps its estimated height forever: tall
            // rows overlap their neighbours and the list never converges.
            // Requesting one more render here guarantees a pass that runs with _jsInstance set.
            StateHasChanged();
            return;
        }

        if (_jsInstance is null)
        {
            return;
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
            // Cast to object so InvokeVoidAsync's params object[] overload treats the descriptor
            // array as ONE argument instead of unpacking each descriptor as a separate JS arg.
            // Without it JS receives setItems(d1, d2, ...) and reads d1 as the whole array - which
            // leaves count unchanged and throws outright when the array is empty.
            await _jsInstance.InvokeVoidAsync("setItems", (object)BuildDescriptors());
        }

        var firstIndex = _virtualItems.Count > 0
            ? _virtualItems[0].Index
            : -1;
        var lastIndex = _virtualItems.Count > 0
            ? _virtualItems[_virtualItems.Count - 1].Index
            : -1;

        // Only re-measure when the rendered window actually changed. Unrelated re-renders (a
        // selection toggle, a state pulse) bring no new DOM into view, and a measure pass there is a
        // querySelectorAll plus a forced layout per row plus an interop round-trip, for nothing.
        // An items change always forces one because the keys behind those indices may differ.
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
    /// Forgets the cached size for one item key. Use when a row's content changed shape without its
    /// key changing - a group row toggling between collapsed and expanded is the usual case. The
    /// next render falls back to the estimate for that key, then measurement refines it.
    /// </summary>
    public async Task InvalidateItemSize(string key)
    {
        if (_jsInstance is null)
        {
            return;
        }

        await _jsInstance.InvokeVoidAsync("invalidateItemSize", key);
    }

    /// <summary>Scrolls so the row at <paramref name="index"/> is positioned per <paramref name="align"/>.</summary>
    public async Task ScrollToIndex(int index, ScrollAlignment align = ScrollAlignment.Start, ScrollBehavior behavior = ScrollBehavior.Auto)
    {
        if (_jsInstance is null)
        {
            return;
        }

        await _jsInstance.InvokeVoidAsync("scrollToIndex", index, align.ToJsName(), behavior.ToJsName());
    }

    /// <summary>
    /// Jumps to the top of the list. Call whenever the item set changes in a way that makes the
    /// previous offset meaningless, such as a new search query.
    /// </summary>
    public async Task ScrollToTop()
    {
        await ScrollToIndex(0, ScrollAlignment.Start, ScrollBehavior.Instant);
    }

    /// <summary>
    /// Snapshots <see cref="Items"/> into the descriptors the JS side needs to answer the
    /// virtualizer's <c>getItemKey</c> and <c>estimateSize</c> callbacks synchronously. Sent once
    /// per <see cref="Items"/> reference change - never on scroll.
    /// </summary>
    private ItemDescriptor[] BuildDescriptors()
    {
        var descriptors = new ItemDescriptor[Items.Count];
        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];
            descriptors[index] = new ItemDescriptor
            {
                Key = ItemKeyExtractor(item),
                EstimatedSize = ItemSizeEstimator?.Invoke(item) ?? EstimatedItemSize,
            };
        }

        return descriptors;
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
