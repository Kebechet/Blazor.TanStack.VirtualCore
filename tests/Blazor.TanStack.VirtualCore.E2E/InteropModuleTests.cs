using System.Text.Json.Serialization;
using Xunit;

namespace Blazor.TanStack.VirtualCore.E2E;

/// <summary>
/// Drives the interop module directly in the browser, with a stand-in for the .NET reference, for
/// behaviour that needs exact timing or exact range arithmetic - neither of which a story offers.
/// </summary>
[Collection(DemoCollectionDefinition.Name)]
public sealed class InteropModuleTests
{
    private const string _story = "components-adaptivevirtualize--imperative-api";

    /// <summary>
    /// Prepended to each test's script. <c>mount</c> creates an instance over 50px items in a
    /// 300x200 viewport, with a .NET stand-in that counts the notifications it receives and hands
    /// each one's total size to an optional <c>onNotify(totalSize, root)</c>.
    /// </summary>
    private const string _mountScript = @"
        const moduleUrl = new URL('_content/Kebechet.Blazor.TanStack.VirtualCore/adaptive-virtualize-interop.js', document.baseURI);
        const { create } = await import(moduleUrl.href);
        const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));
        const mount = (itemCount, options, onNotify) => {
            const viewport = document.createElement('div');
            viewport.style.cssText = 'overflow:auto;width:300px;height:200px;';
            const root = document.createElement('div');
            viewport.appendChild(root);
            document.body.appendChild(viewport);

            const list = { viewport, root, notifications: 0 };
            const dotNetRef = {
                invokeMethodAsync: (method, items, totalSize) => {
                    list.notifications++;
                    onNotify?.(totalSize, root);
                    return Promise.resolve();
                },
            };
            const keys = Array.from({ length: itemCount }, (_, index) => `row-${index}`);
            list.instance = create(root, dotNetRef, keys, keys.map(() => 50), 50, options);
            return list;
        };";

    private readonly DemoFixture _fixture;

    public InteropModuleTests(DemoFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_WithAResizeNotificationStillQueued_NeverNotifiesDotNetAgain(bool useAnimationFrame)
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var notificationsAfterDispose = await _fixture.Page.EvaluateAsync<int>(
            "async useAnimationFrame => {" + _mountScript + @"
                const list = mount(100, { useAnimationFrameWithResizeObserver: useAnimationFrame });
                await nextFrame();
                await nextFrame();

                // An observer created later on the same element is called straight after
                // virtual-core's, so disposing from it lands while the resize's work is still
                // queued. With useAnimationFrameWithResizeObserver virtual-core runs that work in an
                // animation frame, which its teardown has to cancel - 3.17.7 did not, 3.19.4 onwards
                // does. Either way, a notification raised by observer work goes out one task later
                // through this module, which dispose has to cancel too.
                let notificationsAtDispose = 0;
                await new Promise(resolve => {
                    new ResizeObserver((_, observer) => {
                        observer.disconnect();
                        list.instance.dispose();
                        notificationsAtDispose = list.notifications;
                        resolve();
                    }).observe(list.viewport);
                    list.viewport.style.height = '400px';
                });
                await nextFrame();
                await nextFrame();

                list.viewport.remove();
                return list.notifications - notificationsAtDispose;
            }",
            useAnimationFrame);

        // Assert
        // In the app that notification targets a DotNetObjectReference released right after dispose,
        // and Blazor rejects it in the browser console.
        Assert.Equal(0, notificationsAfterDispose);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task DirectionalOverscan_WithLanes_BuffersWholeRows()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var sample = await _fixture.Page.EvaluateAsync<RangeSample>(
            "async () => {" + _mountScript + @"
                // 300 items in 3 lanes: 100 rows of 50px. The module never sizes its own root -
                // Blazor does that from the reported total - so it is sized here, or the viewport
                // would have nothing to scroll.
                const list = mount(300, { lanes: 3, overscanLeading: 4, overscanTrailing: 1 });
                list.root.style.height = `${list.instance.getTotalSize()}px`;
                list.viewport.scrollTop = 2500;
                await nextFrame();
                await nextFrame();

                const snapshot = list.instance.getSnapshot();
                const indexes = list.instance.getVirtualIndexes();
                list.instance.dispose();
                list.viewport.remove();
                return {
                    startIndex: snapshot.startIndex,
                    endIndex: snapshot.endIndex,
                    firstRendered: indexes[0],
                    lastRendered: indexes[indexes.length - 1],
                };
            }");

        // Assert
        // One row of three behind, four rows ahead. Counting single items instead leaves the edge
        // rows partly rendered, so some lanes are buffered less than others.
        Assert.Equal(sample.StartIndex - 3, sample.FirstRendered);
        Assert.Equal(sample.EndIndex + 12, sample.LastRendered);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task EndAnchor_WhenTheLastItemOutgrowsTheContainer_StaysPinnedToTheEnd()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var distanceFromEnd = await _fixture.Page.EvaluateAsync<double>(
            "async () => {" + _mountScript + @"
                const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

                // Five 50px items plus 80px of paddingEnd in a 200px viewport: the end is at
                // scrollTop 130. Blazor sizes the container one interop round-trip after onChange,
                // so the stand-in does the same, a little later.
                const list = mount(5, { anchorTo: 'End', paddingEnd: 80 }, (totalSize, root) =>
                    setTimeout(() => { root.style.height = `${totalSize}px`; }, 30));
                await delay(100);
                list.viewport.scrollTop = 130;
                await delay(300);

                // The end anchor writes scrollTop 200 at once, but the container is still 330px
                // tall, so the browser clamps the write to 130. virtual-core keeps the clamped
                // write and re-issues it from _willUpdate once the container has grown.
                list.instance.resizeItem(4, 120);
                await delay(300);

                const distance = list.viewport.scrollHeight - list.viewport.clientHeight - list.viewport.scrollTop;
                list.instance.dispose();
                list.viewport.remove();
                return distance;
            }");

        // Assert
        // Without the re-issue the list sits exactly the growth short of the end - the grown item's
        // bottom 70px hidden, in the chat-transcript layout End anchoring exists for.
        Assert.Equal(0, distanceFromEnd);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task CancelScroll_AfterAScrollCommand_StopsItCorrectingTowardsTheTarget()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var scrollTops = await _fixture.Page.EvaluateAsync<double[]>(
            "async () => {" + _mountScript + @"
                const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
                const scrollAfterGrowingARowAbove = async cancel => {
                    const list = mount(100, {}, (totalSize, root) =>
                        setTimeout(() => { root.style.height = `${totalSize}px`; }, 30));
                    await delay(100);

                    // Item 50 starts at 2500. Item 10 growing by 150px moves it to 2650 - the same
                    // shift a row measured mid-scroll causes - and an uncancelled command chases it.
                    list.instance.scrollToIndex(50, 'start', 'auto');
                    if (cancel) {
                        list.instance.cancelScroll();
                    }
                    list.instance.resizeItem(10, 200);
                    await delay(300);

                    const scrollTop = list.viewport.scrollTop;
                    list.instance.dispose();
                    list.viewport.remove();
                    return scrollTop;
                };
                return [await scrollAfterGrowingARowAbove(false), await scrollAfterGrowingARowAbove(true)];
            }");

        // Assert
        // The first run proves the command would have moved the viewport; the second that the
        // cancel left it where it was - where a user's own scroll would have taken over.
        Assert.Equal(2650, scrollTops[0]);
        Assert.Equal(2500, scrollTops[1]);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ScrollToIndex_WhileATouchFlingIsMovingTheList_StopsTheMomentumBeforeWriting()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var runs = await _fixture.Page.EvaluateAsync<CommandSample[]>(
            "async () => {" + _mountScript + @"
                const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
                const jumpWhileMoving = async (byTouch, classicScrollbar) => {
                    const list = mount(200, {}, (totalSize, root) =>
                        setTimeout(() => { root.style.height = `${totalSize}px`; }, 30));
                    if (classicScrollbar) {
                        // A scrollbar that takes 15px of the width while it shows; the browser
                        // under test hides its scrollbars.
                        Object.defineProperty(list.viewport, 'clientWidth', {
                            get: () => list.viewport.style.overflowY === 'hidden' ? 300 : 285,
                        });
                    }
                    await delay(100);

                    // The input that set the list moving: a touch that has already lifted - a fling -
                    // or the mouse wheel. A real fling cannot be produced here, so the momentum is the
                    // list being scrolled on after the input.
                    if (byTouch) {
                        list.viewport.dispatchEvent(new PointerEvent('pointerdown', { pointerType: 'touch', bubbles: true }));
                        list.viewport.dispatchEvent(new TouchEvent('touchstart', { bubbles: true }));
                        list.viewport.dispatchEvent(new TouchEvent('touchend', { bubbles: true }));
                    } else {
                        list.viewport.dispatchEvent(new WheelEvent('wheel', { deltaY: 300, bubbles: true }));
                    }
                    list.viewport.scrollTop = 300;
                    await nextFrame();
                    list.viewport.scrollTop = 600;
                    await nextFrame();

                    const command = list.instance.scrollToIndex(100, 'start', 'auto');
                    const sample = {
                        overflowWhenIssued: list.viewport.style.getPropertyValue('overflow-y'),
                        scrollTopWhenIssued: list.viewport.scrollTop,
                    };
                    await command;
                    await delay(100);
                    sample.overflowAfterwards = list.viewport.style.getPropertyValue('overflow-y');
                    sample.scrollTopAfterwards = list.viewport.scrollTop;
                    list.instance.dispose();
                    list.viewport.remove();
                    return sample;
                };
                return [await jumpWhileMoving(true, false), await jumpWhileMoving(false, false), await jumpWhileMoving(true, true)];
            }");

        // Assert
        // Android Chrome carries a fling on after a programmatic scroll write, so the jump would land
        // wherever the momentum takes it. Hiding the overflow for two frames is what stops it.
        var touch = runs[0];
        Assert.Equal("hidden", touch.OverflowWhenIssued);
        Assert.Equal(600, touch.ScrollTopWhenIssued);
        Assert.Equal("auto", touch.OverflowAfterwards);
        Assert.Equal(5000, touch.ScrollTopAfterwards);

        // Nothing to stop after the wheel - on a desktop, hiding the overflow would also hide a
        // classic scrollbar and reflow the list - so the write goes out at once.
        var wheel = runs[1];
        Assert.Equal("auto", wheel.OverflowWhenIssued);
        Assert.Equal(5000, wheel.ScrollTopWhenIssued);

        // A scrollbar that takes layout space would vanish for two frames and reflow the list (a
        // touchscreen laptop), so the overflow is put back before anything paints and the write
        // goes out at once.
        var classicScrollbar = runs[2];
        Assert.Equal("auto", classicScrollbar.OverflowWhenIssued);
        Assert.Equal(5000, classicScrollbar.ScrollTopWhenIssued);
        _fixture.AssertNoJsErrors();
    }

    private sealed class CommandSample
    {
        [JsonPropertyName("overflowWhenIssued")]
        public string OverflowWhenIssued { get; set; } = "";

        [JsonPropertyName("scrollTopWhenIssued")]
        public double ScrollTopWhenIssued { get; set; }

        [JsonPropertyName("overflowAfterwards")]
        public string OverflowAfterwards { get; set; } = "";

        [JsonPropertyName("scrollTopAfterwards")]
        public double ScrollTopAfterwards { get; set; }
    }

    private sealed class RangeSample
    {
        [JsonPropertyName("startIndex")]
        public int StartIndex { get; set; }

        [JsonPropertyName("endIndex")]
        public int EndIndex { get; set; }

        [JsonPropertyName("firstRendered")]
        public int FirstRendered { get; set; }

        [JsonPropertyName("lastRendered")]
        public int LastRendered { get; set; }
    }
}
