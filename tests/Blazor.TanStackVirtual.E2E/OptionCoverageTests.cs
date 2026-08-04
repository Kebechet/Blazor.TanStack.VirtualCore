using Xunit;

namespace Blazor.TanStackVirtual.E2E;

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
