using Xunit;

namespace Blazor.TanStackVirtual.E2E;

/// <summary>
/// One test per public method that reaches virtual-core. Each asserts against what the browser did,
/// read back through <c>GetSnapshot</c> or the live DOM, rather than that the call returned.
/// </summary>
[Collection(DemoCollectionDefinition.Name)]
public sealed class ImperativeApiTests
{
    private const string _story = "components-adaptivevirtualize--imperative-api";

    private readonly DemoFixture _fixture;

    public ImperativeApiTests(DemoFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScrollToOffset_MovesToTheRequestedPixelOffset()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync(_story);

        // Act
        await canvas.GetByTestId("api-scroll-offset").ClickAsync();
        var state = await StoryState.WaitForAsync(
            canvas,
            "api-state",
            x => x.TryGetProperty("scrollOffset", out var o)
                && o.ValueKind != System.Text.Json.JsonValueKind.Null
                && Math.Abs(o.GetDouble() - 2000) < 30,
            "the scroll offset to settle near 2000");

        // Assert
        // GetSnapshot reads scrollOffset off the live virtualizer, so this is the browser's own
        // number rather than the argument echoed back.
        var offset = state.GetProperty("scrollOffset").GetDouble();
        Assert.True(
            Math.Abs(offset - 2000) < 30,
            $"Expected to land near offset 2000, snapshot reported {offset}.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ScrollToEnd_ReachesTheEndOfTheList()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync(_story);

        // Act
        await canvas.GetByTestId("api-scroll-end").ClickAsync();
        var state = await StoryState.WaitForAsync(
            canvas,
            "api-state",
            x => x.TryGetProperty("isAtEnd", out var atEnd) && atEnd.ValueKind == System.Text.Json.JsonValueKind.True,
            "the virtualizer to report it is at the end");

        // Assert
        Assert.True(state.GetProperty("isAtEnd").GetBoolean());
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ScrollBy_AdvancesByTheGivenDelta()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync(_story);
        await canvas.GetByTestId("api-snapshot").ClickAsync();
        var before = await StoryState.WaitForAsync(
            canvas,
            "api-state",
            x => x.TryGetProperty("scrollOffset", out var o) && o.ValueKind != System.Text.Json.JsonValueKind.Null,
            "an initial snapshot");
        var beforeOffset = before.GetProperty("scrollOffset").GetDouble();

        // Act
        await canvas.GetByTestId("api-scroll-by").ClickAsync();
        var after = await StoryState.WaitForAsync(
            canvas,
            "api-state",
            x => x.GetProperty("scrollOffset").GetDouble() > beforeOffset,
            "the offset to advance");

        // Assert
        var delta = after.GetProperty("scrollOffset").GetDouble() - beforeOffset;
        Assert.True(
            Math.Abs(delta - 300) < 30,
            $"Expected a ~300px nudge, measured {delta:F0}px.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ResizeItem_OverridesAMeasuredSizeWithoutRemeasuringTheDom()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync(_story);
        await canvas.GetByTestId("api-snapshot").ClickAsync();
        var before = await StoryState.WaitForAsync(
            canvas,
            "api-state",
            x => x.TryGetProperty("totalSize", out var t) && t.ValueKind != System.Text.Json.JsonValueKind.Null,
            "an initial snapshot");
        var beforeTotal = before.GetProperty("totalSize").GetDouble();

        // Act
        await canvas.GetByTestId("api-resize").ClickAsync();
        var after = await StoryState.WaitForAsync(
            canvas,
            "api-state",
            x => Math.Abs(x.GetProperty("totalSize").GetDouble() - beforeTotal) > 1,
            "the total size to change after the forced resize");

        // Assert
        // Forcing item 0 to 200px must move the list's total size. If ResizeItem never reached
        // virtual-core, the total would be unchanged and this would time out.
        Assert.NotEqual(beforeTotal, after.GetProperty("totalSize").GetDouble());
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task GetSnapshot_ReportsTheRenderedRangeMatchingTheDom()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync(_story);

        // Act
        await canvas.GetByTestId("api-snapshot").ClickAsync();
        var state = await StoryState.WaitForAsync(
            canvas,
            "api-state",
            x => x.TryGetProperty("startIndex", out var s) && s.ValueKind != System.Text.Json.JsonValueKind.Null && s.GetInt32() >= 0,
            "a snapshot carrying the rendered range");
        var rows = await _fixture.ReadRenderedRowsAsync();

        // Assert
        // The snapshot's range and the actual DOM must agree; a mismatch means .NET is reasoning
        // about a window the browser is not rendering.
        var startIndex = state.GetProperty("startIndex").GetInt32();
        Assert.True(
            rows.Any(x => x.Index == startIndex),
            $"Snapshot reported startIndex {startIndex}, which is not among the rendered indices.");
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task FollowOnAppend_KeepsTheViewportAtTheEndWhenItemsAreAdded()
    {
        // Arrange
        var canvas = await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--follow-on-append");
        await canvas.GetByTestId("follow-scroll-end").ClickAsync();
        await StoryState.WaitForAsync(
            canvas,
            "follow-state",
            x => x.TryGetProperty("isAtEnd", out var atEnd) && atEnd.ValueKind == System.Text.Json.JsonValueKind.True,
            "the list to reach the end before appending");

        // Act
        await canvas.GetByTestId("follow-append").ClickAsync();
        // The follow is scheduled by setOptions into pendingScrollAnchor and only executed by
        // _willUpdate - which the interop must call after an item-set change. The first version of
        // this test asserted only that the item count grew, which passes even when the viewport is
        // stranded behind the appended rows.
        var state = await StoryState.WaitForAsync(
            canvas,
            "follow-state",
            x => StoryState.Int(x, "itemCount") >= 310 &&
                x.TryGetProperty("endIndex", out var endIndex) &&
                endIndex.ValueKind != System.Text.Json.JsonValueKind.Null &&
                endIndex.GetInt32() >= 309,
            "the viewport to follow the appended items to the new end");

        // Assert
        // The rendered window must now contain the LAST appended item - the viewport followed.
        Assert.True(StoryState.Int(state, "endIndex") >= 309, $"Viewport did not follow. State: {state}");
        var rows = await _fixture.ReadRenderedRowsAsync();
        Assert.Contains(rows, row => row.Index == 309);
        _fixture.AssertNoJsErrors();
    }
}
