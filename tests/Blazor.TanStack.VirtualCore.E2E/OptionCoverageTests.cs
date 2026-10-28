using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;
using Xunit;

namespace Blazor.TanStack.VirtualCore.E2E;

/// <summary>
/// One test per option that changes observable layout or behaviour. An option that serializes
/// correctly but never reaches virtual-core produces a list that looks plausible and ignores the
/// setting, which is exactly the failure a unit test cannot see.
/// </summary>
[Collection(DemoCollectionDefinition.Name)]
public sealed class OptionCoverageTests
{
    private readonly DemoFixture _fixture;

    public OptionCoverageTests(DemoFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IsHorizontal_TranslatesAlongXRatherThanY()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--horizontal");

        // Act
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // A horizontal list that quietly fell back to vertical would still render and still scroll
        // its container - the axis of the transform is the only thing that distinguishes them.
        Assert.True(rows.Count > 1, "Expected several cards.");
        Assert.True(
            rows[^1].TranslateX > 0,
            $"Expected cards laid out along x, last card translateX was {rows[^1].TranslateX}.");
        Assert.True(
            rows.All(x => Math.Abs(x.TranslateY) < 0.5),
            "Expected no y translation on a horizontal list.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task Lanes_DistributesItemsAcrossDistinctCrossAxisPositions()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--lanes-masonry-");

        // Act
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // Three lanes means three distinct left offsets. If lanes never reached virtual-core, every
        // item would report lane 0 and stack in a single full-width column.
        var distinctLanes = rows.Select(x => x.CrossStart).Distinct().Count();
        Assert.True(
            distinctLanes >= 2,
            $"Expected items spread across lanes, found {distinctLanes} distinct cross-axis positions.");
        // Each lane is a third of the container, so no item should span the full width.
        Assert.True(
            rows.All(x => x.Width > 0),
            "Expected every item to have a measurable width.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task GapAndPadding_ShiftTheFirstItemAndSeparateNeighbours()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--gap-and-padding");
        await StoryState.WaitForAsync(
            canvas,
            "spacing-state",
            x => StoryState.Int(x, "measuredPasses") > 0,
            "the first measurement pass to complete");

        // Act
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // PaddingStart is virtualizer geometry, not CSS, so it must show up in the first item's own
        // offset. A CSS margin would leave this at zero.
        Assert.True(
            rows[0].TranslateY >= 39,
            $"Expected the first item to start after the 40px PaddingStart, was {rows[0].TranslateY:F1}px.");

        // Consecutive items must be separated by their size plus the 16px Gap.
        for (var index = 1; index < rows.Count; index++)
        {
            var previous = rows[index - 1];
            var current = rows[index];
            var separation = current.TranslateY - (previous.TranslateY + previous.Height);
            Assert.True(
                Math.Abs(separation - 16) < 1.5,
                $"Expected a 16px gap between items {previous.Index} and {current.Index}, measured {separation:F1}px.");
        }

        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task AlwaysRenderedIndexes_KeepsPinnedItemsInTheDomAfterScrollingAway()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--always-rendered-indexes");

        // Act
        await canvas.GetByTestId("pinned-scroll-target").ClickAsync();
        await _fixture.Page.WaitForFunctionAsync(
            @"() => Array.from(document.querySelectorAll('[data-index]'))
				.some(el => parseInt(el.getAttribute('data-index'), 10) > 1500)",
            null,
            new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 15_000 });

        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // Rows 0 and 1 are 2,000 items behind the viewport. Only the compiled rangeExtractor keeps
        // them rendered; without it they would have been recycled long ago.
        Assert.Contains(rows, x => x.Index == 0);
        Assert.Contains(rows, x => x.Index == 1);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task DirectionalOverscan_WhileScrollingForward_BuffersFurtherAheadThanBehind()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--directional-overscan");
        await JumpToRow2000Async(canvas);

        // Act
        await _fixture.ScrollViewportAsync("directional", 600);
        var buffer = await ReadBufferAsync(canvas);

        // Assert
        // The snapshot is read through a .NET round-trip, which on a loaded machine can land after
        // virtual-core's 150ms isScrolling reset - after which it reports no direction. The buffer
        // below is what carries the claim, and it is held either way.
        Assert.True(
            buffer.ScrollDirection is "forward" or null,
            $"Expected a forward scroll, the snapshot reported {buffer.ScrollDirection}.");

        // OverscanLeading 12 against OverscanTrailing 2. A build where the compiled rangeExtractor
        // never reached virtual-core still renders a correct, scrollable list - it is simply
        // symmetric, which only this comparison can see.
        Assert.Equal(12, buffer.Below);
        Assert.Equal(2, buffer.Above);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task DirectionalOverscan_AfterReversing_FlipsWhichSideIsBuffered()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--directional-overscan");
        await JumpToRow2000Async(canvas);
        await _fixture.ScrollViewportAsync("directional", 600);

        // Act
        await _fixture.ScrollViewportAsync("directional", -600);
        var buffer = await ReadBufferAsync(canvas);

        // Assert
        // The leading side is wherever the user is heading, not a fixed downward bias - otherwise
        // scrolling back up would run permanently on the small buffer. As above, a late read finds
        // no direction; with none, the buffer would revert to forward, so the counts still pin the flip.
        Assert.True(
            buffer.ScrollDirection is "backward" or null,
            $"Expected a backward scroll, the snapshot reported {buffer.ScrollDirection}.");
        Assert.Equal(12, buffer.Above);
        Assert.Equal(2, buffer.Below);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task DirectionalOverscan_OnceScrollingStops_HoldsTheTrailingFloor()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--directional-overscan");
        await JumpToRow2000Async(canvas);

        // Act - well past virtual-core's default 150ms isScrollingResetDelay
        await _fixture.ScrollViewportAsync("directional", 600);
        await Task.Delay(1000);
        var buffer = await ReadBufferAsync(canvas);

        // Assert
        // Pins the reason the trailing count is a held floor rather than a while-scrolling value:
        // virtual-core memoizes getVirtualIndexes on [rangeExtractor, overscan, count, startIndex,
        // endIndex, lanes], so nothing re-invokes the extractor once the range stops moving. A value
        // that lapsed on isScrolling would never be recomputed, and the bias would outlive the scroll.
        // Holding it keeps a reversal no worse off than a symmetric window of the same size.
        Assert.False(buffer.IsScrolling);
        Assert.Equal(2, buffer.Above);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>Clicks "Scroll to 2000" and waits for the jump to have landed.</summary>
    /// <remarks>
    /// The directional tests scroll by hand straight after, and a hand scroll issued while the
    /// component's own scroll command is still being applied leaves the outcome to whichever write
    /// the browser handles last.
    /// </remarks>
    private static async Task JumpToRow2000Async(ILocator canvas)
    {
        await canvas.GetByTestId("directional-scroll-target").ClickAsync();
        await StoryState.WaitForAsync(
            canvas,
            "directional-state",
            x => x.GetProperty("lastAction").GetString() == "scrollToIndex:2000"
                && x.TryGetProperty("startIndex", out var start) && start.ValueKind == JsonValueKind.Number
                && Math.Abs(start.GetInt32() - 2000) <= 1
                && x.TryGetProperty("isScrolling", out var scrolling) && scrolling.ValueKind == JsonValueKind.False,
            "the jump to row 2000 to land");
    }

    /// <summary>
    /// How many rendered rows sit outside the visible range on each side - the overscan buffer, as
    /// the difference between what is in the DOM and the range the virtualizer reports as visible.
    /// </summary>
    /// <remarks>
    /// Index arithmetic rather than geometry: the rendered rows are absolutely positioned inside the
    /// virtualizer's own container, so comparing their <c>translateY</c> against the viewport's
    /// <c>scrollTop</c> silently measures against the wrong origin whenever anything else shares the
    /// scroller.
    /// </remarks>
    private async Task<BufferSample> ReadBufferAsync(ILocator canvas)
    {
        await canvas.GetByTestId("directional-snapshot").ClickAsync();
        var state = await StoryState.WaitForAsync(
            canvas,
            "directional-state",
            x => x.TryGetProperty("startIndex", out var start) && start.GetInt32() >= 0,
            "a snapshot carrying the visible range");

        var rows = await _fixture.ReadRenderedRowsAsync();

        return new BufferSample
        {
            Above = StoryState.Int(state, "startIndex") - rows.Min(x => x.Index),
            Below = rows.Max(x => x.Index) - StoryState.Int(state, "endIndex"),
            IsScrolling = StoryState.Bool(state, "isScrolling"),
            ScrollDirection = state.GetProperty("scrollDirection").GetString(),
        };
    }

    private sealed class BufferSample
    {
        public required int Above { get; init; }
        public required int Below { get; init; }
        public required bool IsScrolling { get; init; }
        public required string? ScrollDirection { get; init; }
    }

    [Fact]
    public async Task IsEnabledFalse_ResetsTheWindow_AndReEnablingRestoresIt()
    {
        // Arrange
        // Upstream's documented behaviour for enabled=false is a RESET, not a freeze: it empties
        // getVirtualItems() and clears the measurement caches, so every rendered item unmounts.
        // The first version of this test asserted a "frozen" window and passed vacuously by
        // comparing one empty list with another.
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--disabled");
        var enabled = await _fixture.ReadRenderedRowsAsync();
        Assert.True(enabled.Count > 0, "Expected rows while enabled.");

        // Act
        await canvas.GetByTestId("disabled-toggle-enabled").ClickAsync();
        await StoryState.WaitForAsync(
            canvas,
            "disabled-state",
            x => x.GetProperty("lastAction").GetString() == "isEnabled:False",
            "the virtualizer to be suspended");
        var whileDisabled = await _fixture.ReadRenderedRowsAsync();

        await canvas.GetByTestId("disabled-toggle-enabled").ClickAsync();
        await _fixture.Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('[data-index]').length > 0",
            null,
            new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 15_000 });
        var reEnabled = await _fixture.ReadRenderedRowsAsync();

        // Assert
        Assert.Empty(whileDisabled);
        Assert.True(reEnabled.Count > 0, "Expected the window to come back after re-enabling.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task UseWindowScroller_VirtualizesAgainstTheDocumentInsteadOfAContainer()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--window-scroller");
        var before = await _fixture.ReadRenderedRowsAsync();

        // Act
        // Scrolling the window rather than any element is the whole point of this mode.
        await _fixture.Page.EvaluateAsync("() => window.scrollTo(0, 3000)");
        await _fixture.Page.WaitForFunctionAsync(
            $"first => {{ const els = document.querySelectorAll('[data-index]'); if (!els.length) return false; " +
            $"return Math.min(...Array.from(els).map(el => parseInt(el.getAttribute('data-index'), 10))) !== first; }}",
            before[0].Index,
            new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 15_000 });

        var after = await _fixture.ReadRenderedRowsAsync();

        // Assert
        Assert.True(
            after[0].Index > before[0].Index,
            "Expected the window-scrolled list to advance when the document scrolled.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task IsRtl_RailStartsOnTheRight_AndVirtualizesAsItScrollsLeft()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--right-to-left");
        var atStart = await ReadRtlRailAsync(targetIndex: 0);

        // Act
        // In a right-to-left container scrollLeft starts at 0 and goes negative as the user moves on.
        await _fixture.Page.EvalOnSelectorAsync("[data-testid=\"rtl-viewport\"]", "el => el.scrollLeft -= 4000");
        await Task.Delay(1000);
        var scrolled = await ReadRtlRailAsync(targetIndex: 0);

        // Assert
        // Item 0 sits against the right edge, where a right-to-left reader starts.
        Assert.True(
            Math.Abs(atStart.TargetRight - atStart.ViewportRight) <= 2,
            $"Expected item 0 against the viewport's right edge ({atStart.ViewportRight}), it ends at {atStart.TargetRight}.");
        // 4000px on, the rendered window has moved with the scroll and is what is on screen - not
        // the blank space past items that were never rendered.
        Assert.True(scrolled.FirstIndex > 0, $"Expected the rendered window to move on, it still starts at {scrolled.FirstIndex}.");
        Assert.True(scrolled.VisibleItems > 0, "Expected rendered items on screen after scrolling, there were none.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task IsRtl_ScrollToIndex_BringsTheItemOnScreen()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--right-to-left");

        // Act
        await canvas.GetByTestId("rtl-scroll-target").ClickAsync();
        await Task.Delay(1000);
        var rail = await ReadRtlRailAsync(targetIndex: 500);

        // Assert
        // virtual-core reads the offset negated under isRtl but writes it as is, and a positive
        // scrollLeft in a right-to-left container clamps to the start - the jump would go nowhere.
        Assert.True(rail.TargetVisible, $"Expected item 500 on screen; the rendered window is {rail.FirstIndex}-{rail.LastIndex}.");
        _fixture.AssertNoJsErrors();
    }

    private async Task<RtlRail> ReadRtlRailAsync(int targetIndex)
    {
        return await _fixture.Page.EvaluateAsync<RtlRail>(
            @"targetIndex => {
                const viewport = document.querySelector('[data-testid=""rtl-viewport""]');
                const box = viewport.getBoundingClientRect();
                // The content box: inside the border, which is what the rail is laid out against.
                const left = box.left + viewport.clientLeft;
                const right = left + viewport.clientWidth;
                const items = Array.from(document.querySelectorAll('[data-index]'))
                    .map(el => ({ index: +el.getAttribute('data-index'), rect: el.getBoundingClientRect() }));
                const onScreen = rect => rect.right > left + 1 && rect.left < right - 1;
                const target = items.find(item => item.index === targetIndex);
                return {
                    viewportRight: right,
                    targetRight: target ? target.rect.right : -1,
                    targetVisible: !!target && onScreen(target.rect),
                    firstIndex: Math.min(...items.map(item => item.index)),
                    lastIndex: Math.max(...items.map(item => item.index)),
                    visibleItems: items.filter(item => onScreen(item.rect)).length,
                };
            }",
            targetIndex);
    }

    private sealed class RtlRail
    {
        [JsonPropertyName("viewportRight")]
        public double ViewportRight { get; set; }

        [JsonPropertyName("targetRight")]
        public double TargetRight { get; set; }

        [JsonPropertyName("targetVisible")]
        public bool TargetVisible { get; set; }

        [JsonPropertyName("firstIndex")]
        public int FirstIndex { get; set; }

        [JsonPropertyName("lastIndex")]
        public int LastIndex { get; set; }

        [JsonPropertyName("visibleItems")]
        public int VisibleItems { get; set; }
    }
}
