using Xunit;

namespace Blazor.TanStack.VirtualCore.E2E;

/// <summary>
/// Virtualization is only observable at runtime: how many elements exist, where the browser put
/// them, and whether they moved when you scrolled. bUnit stubs the interop out entirely, so none of
/// these claims are reachable from a unit test.
/// </summary>
[Collection(DemoCollectionDefinition.Name)]
public sealed class VirtualizationTests
{
    private readonly DemoFixture _fixture;

    public VirtualizationTests(DemoFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TenThousandRows_OnlyAWindowExistsInTheDom()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--ten-thousand-rows");

        // Act
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // The list holds 10,000 items in a 420px viewport. Anything approaching that count in the
        // DOM means the component rendered everything and virtualization did nothing.
        Assert.True(rows.Count > 0, "Expected the virtualizer to render some rows.");
        Assert.True(
            rows.Count < 100,
            $"Expected only a window of rows in the DOM, found {rows.Count} of 10,000.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task Scrolling_ShiftsTheRenderedWindowWithoutGrowingIt()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--ten-thousand-rows");
        var before = await _fixture.ReadRenderedRowsAsync();

        // Act
        await _fixture.ScrollViewportAsync("uniform", 4000);
        var after = await _fixture.ReadRenderedRowsAsync();

        // Assert
        Assert.True(after[0].Index > before[0].Index, "Expected the window to move forward.");
        // A window that grows as you scroll is a leak - rows being added without old ones being
        // removed - which looks fine on screen right up until the list is long enough to matter.
        Assert.True(
            after.Count < before.Count + 20,
            $"Expected a stable window size, went from {before.Count} to {after.Count} rows.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task RenderedRows_AreStackedWithoutGapsOrOverlaps()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--variable-heights");
        // Rows are first placed at their estimates and only then refined by the measurement pass,
        // so reading before that lands would assert against the estimates, not the measurements.
        await StoryState.WaitForAsync(
            canvas,
            "variable-state",
            x => StoryState.Int(x, "measuredPasses") > 0,
            "the first measurement pass to complete");

        // Act
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // Each row's offset must equal the previous row's offset plus its measured height. This is
        // the assertion that a single averaged item size cannot satisfy on a variable-height list:
        // it is exactly where short rows drift apart and tall ones overlap.
        Assert.True(rows.Count > 2, "Expected several rows to compare.");
        for (var index = 1; index < rows.Count; index++)
        {
            var previous = rows[index - 1];
            var current = rows[index];
            var expectedOffset = previous.Offset(false) + previous.Height;

            Assert.True(
                Math.Abs(current.Offset(false) - expectedOffset) < 1.5,
                $"Row {current.Index} sits at {current.Offset(false):F1}px but row {previous.Index} ends at {expectedOffset:F1}px.");
        }

        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task VariableHeights_AreMeasuredIndividuallyRatherThanAveraged()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--variable-heights");

        // Act
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // The story cycles 1/8/25/3-word bodies, so real heights differ several-fold. If the
        // component were placing rows from one estimate, every rendered height would be identical.
        var distinctHeights = rows.Select(x => Math.Round(x.Height)).Distinct().Count();
        Assert.True(
            distinctHeights > 1,
            $"Expected varied row heights, every rendered row measured the same. Rows: {rows.Count}");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ScrollToIndex_LandsOnTheRequestedRow()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--imperative-api");

        // Act
        await canvas.GetByTestId("api-scroll-target").ClickAsync();
        await _fixture.Page.WaitForFunctionAsync(
            @"() => Array.from(document.querySelectorAll('[data-index]'))
				.some(el => parseInt(el.getAttribute('data-index'), 10) === 12345)",
            null,
            new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 15_000 });

        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // Landing on an exact index 12,345 rows into a mixed-height list is only possible because
        // each preceding row's real size is known - an estimate-based offset would land near it.
        Assert.Contains(rows, row => row.Index == 12345);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ScrollToTop_ReturnsToTheFirstRow()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--imperative-api");
        await canvas.GetByTestId("api-scroll-target").ClickAsync();
        await _fixture.Page.WaitForFunctionAsync(
            @"() => Array.from(document.querySelectorAll('[data-index]'))
				.every(el => parseInt(el.getAttribute('data-index'), 10) > 100)",
            null,
            new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 15_000 });

        // Act
        await canvas.GetByTestId("api-scroll-top").ClickAsync();
        await _fixture.Page.WaitForFunctionAsync(
            @"() => Array.from(document.querySelectorAll('[data-index]'))
				.some(el => parseInt(el.getAttribute('data-index'), 10) === 0)",
            null,
            new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 15_000 });

        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        Assert.Equal(0, rows[0].Index);
        Assert.Equal(0, rows[0].Offset(false));
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ExpandingAGroup_InsertsChildrenWithoutOverlappingTheGroupRow()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--expand-and-collapse");

        // Act
        // Clicking the first group changes its own height while its key stays the same, which is
        // what InvalidateItemSize exists for.
        // The handler sits on the row's inner div, not on the [data-index] wrapper the component
        // renders. Clicking the wrapper would land on an element with no handler, and DOM events
        // bubble up rather than down, so nothing would fire.
        await _fixture.Page.ClickAsync("[data-index='0'] .row-group");
        // Waiting on the rendered element COUNT would hang forever: virtualization keeps roughly
        // the same number of rows in the viewport no matter how many items the list holds, so
        // inserting three children changes the item count without changing the DOM count. The
        // children appearing is the actual signal.
        await _fixture.Page.WaitForSelectorAsync(
            ".row-child",
            new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 15_000 });

        var after = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // Without the invalidation the group keeps its stale collapsed height and the first child
        // renders on top of its bottom edge - which shows up here as a negative gap.
        for (var index = 1; index < after.Count; index++)
        {
            var previous = after[index - 1];
            var current = after[index];
            Assert.True(
                current.Offset(false) >= previous.Offset(false) + previous.Height - 1.5,
                $"Row {current.Index} at {current.Offset(false):F1}px overlaps row {previous.Index}, which ends at {previous.Offset(false) + previous.Height:F1}px.");
        }

        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task LayoutMeasuredCallback_FiresAfterMeasurement()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--variable-heights");

        // Act
        var state = await StoryState.WaitForAsync(
            canvas,
            "variable-state",
            x => StoryState.Int(x, "measuredPasses") > 0,
            "OnLayoutMeasured to fire at least once");

        // Assert
        // The callback is what lets a consumer drive two-phase rendering; if it never fires, that
        // pattern silently degrades to estimate-then-snap.
        Assert.True(StoryState.Int(state, "measuredPasses") > 0);
        _fixture.AssertNoJsErrors();
    }
}
