using System.Text.Json.Serialization;
using Xunit;

namespace Blazor.TanStack.VirtualCore.E2E;

/// <summary>
/// Placements reach the DOM through a .NET render, one interop round trip after the virtualizer computed them. On
/// a phone's WebView that round trip takes hundreds of milliseconds (~250 ms measured on a Samsung A53), where the
/// demo's in-browser WebAssembly answers within a frame. These tests hold every placement notification back to
/// reproduce the phone, and check what is painted in the meantime.
/// </summary>
[Collection(DemoCollectionDefinition.Name)]
public sealed class SlowInteropTests
{
    /// <summary>Longer than every sample window below, so no placement from .NET lands inside one.</summary>
    private const int PlacementDelayMilliseconds = 600;

    private const int SampleDurationMilliseconds = 400;

    /// <summary>Sub-pixel rounding between a row's bottom edge and the next row's top.</summary>
    private const double OverlapTolerancePx = 1.5;

    private readonly DemoFixture _fixture;

    public SlowInteropTests(DemoFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A row whose content grows without .NET knowing - an image arriving, a child rendering late - used to paint
    /// over the rows below it until .NET re-rendered their placements.
    /// </summary>
    [Fact]
    public async Task RowGrowing_UnderSlowInterop_RowsBelowMoveInTheSameFrame()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--variable-heights");
        await HoldBackPlacementsAsync();
        var heightBefore = await _fixture.Page.EvaluateAsync<double>(
            "() => document.querySelector(\"[data-index='0']\").getBoundingClientRect().height");

        // Act
        await StartSamplingPaintedFramesAsync();
        await _fixture.Page.EvaluateAsync(
            @"() => requestAnimationFrame(() => {
                const content = document.querySelector(""[data-index='0']"").firstElementChild;
                content.style.minHeight = `${content.getBoundingClientRect().height + 120}px`;
            })");
        var frames = await CollectPaintedFramesAsync();

        // Assert
        Assert.Contains(frames, frame => frame.Rows.Any(row => row.Index == 0 && row.Bottom - row.Top > heightBefore + 100));
        AssertNoVisibleRowsOverlap(frames);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>
    /// Expanding a group inserts rows, which moves every following item to another index. Until fresh placements
    /// came back from .NET those rows were hidden, so the list below the group went blank for a whole round trip.
    /// </summary>
    [Fact]
    public async Task ExpandingAGroup_UnderSlowInterop_ShowsTheRowsBelowBeforeDotNetAnswers()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync("components-adaptivevirtualize--expand-and-collapse");
        await HoldBackPlacementsAsync();

        // Act
        await StartSamplingPaintedFramesAsync();
        await _fixture.Page.ClickAsync("[data-index='0'] .row-group");
        var frames = await CollectPaintedFramesAsync();

        // Assert
        Assert.Contains(frames, frame => frame.Rows.Any(row => row.IsChild && !row.IsHidden));
        // One frame is allowed: the render that hides the moved rows can be painted before setItems reaches JS.
        var framesWithHiddenRows = frames.Count(frame => frame.Rows.Any(row => row.IsHidden));
        Assert.True(framesWithHiddenRows <= 1, $"{framesWithHiddenRows} of {frames.Count} painted frames had hidden rows.");
        AssertNoVisibleRowsOverlap(frames);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>
    /// Delays every OnVirtualItemsChanged call, keeping their order, the way a slow WebView bridge does. Only that
    /// direction is held back: it is the one placements travel, while calls from .NET into JS stay prompt.
    /// </summary>
    private Task HoldBackPlacementsAsync()
    {
        return _fixture.Page.EvaluateAsync(
            @"delayMs => {
                const prototype = DotNet.DotNetObject.prototype;
                const invoke = prototype.invokeMethodAsync;
                let previous = Promise.resolve();
                prototype.invokeMethodAsync = function (name, ...args) {
                    if (name !== 'OnVirtualItemsChanged') {
                        return invoke.call(this, name, ...args);
                    }
                    const call = previous
                        .then(() => new Promise(resolve => setTimeout(resolve, delayMs)))
                        .then(() => invoke.call(this, name, ...args));
                    previous = call.catch(() => {});
                    return call;
                };
            }",
            PlacementDelayMilliseconds);
    }

    /// <summary>
    /// Records every rendered row once per frame, after the frame has been painted: a task queued from a
    /// requestAnimationFrame callback runs only once that frame's layout, ResizeObserver callbacks and paint are
    /// done, so it sees what the user saw rather than an intermediate layout.
    /// </summary>
    private Task StartSamplingPaintedFramesAsync()
    {
        return _fixture.Page.EvaluateAsync(
            @"durationMs => {
                const frames = [];
                window.__paintedFrames = new Promise(resolve => {
                    const startedAt = performance.now();
                    const sample = () => frames.push({
                        rows: Array.from(document.querySelectorAll('[data-index]')).map(el => {
                            const rect = el.getBoundingClientRect();
                            return {
                                index: parseInt(el.getAttribute('data-index'), 10),
                                top: rect.top,
                                bottom: rect.bottom,
                                isHidden: getComputedStyle(el).visibility === 'hidden',
                                isChild: el.querySelector('.row-child') !== null,
                            };
                        }),
                    });
                    const tick = () => {
                        setTimeout(sample, 0);
                        if (performance.now() - startedAt < durationMs) {
                            requestAnimationFrame(tick);
                        } else {
                            setTimeout(() => resolve(frames), 50);
                        }
                    };
                    requestAnimationFrame(tick);
                });
            }",
            SampleDurationMilliseconds);
    }

    private async Task<IReadOnlyList<PaintedFrame>> CollectPaintedFramesAsync()
    {
        return await _fixture.Page.EvaluateAsync<PaintedFrame[]>("() => window.__paintedFrames");
    }

    private static void AssertNoVisibleRowsOverlap(IReadOnlyList<PaintedFrame> frames)
    {
        foreach (var (frameIndex, frame) in frames.Index())
        {
            var visibleRows = frame.Rows
                .Where(row => !row.IsHidden && row.Bottom > row.Top)
                .OrderBy(row => row.Top)
                .ToList();
            for (var index = 1; index < visibleRows.Count; index++)
            {
                var previous = visibleRows[index - 1];
                var current = visibleRows[index];
                Assert.True(
                    current.Top >= previous.Bottom - OverlapTolerancePx,
                    $"Frame {frameIndex}: row {current.Index} at {current.Top:F1}px overlaps row {previous.Index}, which ends at {previous.Bottom:F1}px.");
            }
        }
    }

    private sealed class PaintedFrame
    {
        [JsonPropertyName("rows")]
        public PaintedRow[] Rows { get; set; } = [];
    }

    private sealed class PaintedRow
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("top")]
        public double Top { get; set; }

        [JsonPropertyName("bottom")]
        public double Bottom { get; set; }

        [JsonPropertyName("isHidden")]
        public bool IsHidden { get; set; }

        [JsonPropertyName("isChild")]
        public bool IsChild { get; set; }
    }
}
