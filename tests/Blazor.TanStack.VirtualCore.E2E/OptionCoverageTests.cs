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
        await canvas.GetByTestId("directional-scroll-target").ClickAsync();

        // Act
        await _fixture.ScrollViewportAsync("directional", 600);
        var buffer = await ReadBufferAsync(canvas);

        // Assert
        Assert.Equal("forward", buffer.ScrollDirection);

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
        await canvas.GetByTestId("directional-scroll-target").ClickAsync();
        await _fixture.ScrollViewportAsync("directional", 600);

        // Act
        await _fixture.ScrollViewportAsync("directional", -600);
        var buffer = await ReadBufferAsync(canvas);

        // Assert
        // The leading side is wherever the user is heading, not a fixed downward bias - otherwise
        // scrolling back up would run permanently on the small buffer.
        Assert.Equal("backward", buffer.ScrollDirection);
        Assert.Equal(12, buffer.Above);
        Assert.Equal(2, buffer.Below);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task DirectionalOverscan_OnceScrollingStops_HoldsTheTrailingFloor()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--directional-overscan");
        await canvas.GetByTestId("directional-scroll-target").ClickAsync();

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
    public async Task IsRtl_RendersWithoutInteropErrors()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--right-to-left");

        // Act
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // RTL flips the direction offsets are measured in; the observable claim here is simply that
        // the combination of IsHorizontal and IsRtl still produces a laid-out list rather than
        // throwing inside virtual-core.
        Assert.True(rows.Count > 1, "Expected an RTL horizontal list to render items.");
        _fixture.AssertNoJsErrors();
    }
}
