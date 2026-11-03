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

    /// <summary>
    /// Appended to <see cref="_mountScript"/> by tests that need measured rows. <c>mountMeasured</c>
    /// mounts a list with 2000px to scroll in, then renders rows 0-2 at 80, 30 and 60px and measures
    /// them as a Blazor render and <c>measureRenderedRows</c> do. virtual-core observes each row's
    /// size from then on.
    /// </summary>
    private const string _measuredRowsScript = @"
        const mountMeasured = async (itemCount, options) => {
            const list = mount(itemCount, options);
            list.root.style.height = '2000px';
            // Measured a frame later, as in the app. In the constructor's own task a notification is
            // still held back, which would hide the measurement from whatever reads it.
            await nextFrame();
            await nextFrame();
            list.rows = [80, 30, 60].map((height, index) => {
                const row = document.createElement('div');
                row.setAttribute('data-index', String(index));
                row.style.height = `${height}px`;
                list.root.appendChild(row);
                return row;
            });
            list.instance.measureRenderedRows();
            await nextFrame();
            await nextFrame();
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

    /// <summary>
    /// A group expanding calls InvalidateItemSize, renders the grown row, and only then sends the new item set - in
    /// the app a separate interop message, which the ResizeObserver measuring the grown row gets ahead of. setItems
    /// keeps only sizes it knows to be measured, and dropping that one put every row below back on the estimate
    /// until the next measureRenderedRows, half a round trip later: on a phone, long enough to see them overlap.
    /// </summary>
    [Fact]
    public async Task SetItems_AfterAResizeObserverMeasuredAnInvalidatedRow_KeepsItsNewSize()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var secondRowTransform = await _fixture.Page.EvaluateAsync<string>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(100, {});
                list.rows.forEach((row, index) => row.setAttribute('data-key', `row-${index}`));
                list.instance.invalidateItemSize('row-0');
                list.rows[0].style.height = '200px';
                await nextFrame();
                await nextFrame();

                const keys = Array.from({ length: 100 }, (_, index) => `row-${index}`);
                list.instance.setItems(keys.map((_, index) => index), keys, keys.map(() => 50), true);
                const transform = list.rows[1].style.transform;

                list.viewport.remove();
                return transform;
            }");

        // Assert
        // The estimate would have put row 1 at 50px.
        Assert.Equal("translateY(200px)", secondRowTransform);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>
    /// .NET sends a new item set ahead of the render that shows it, so after InvalidateItemSize the set can arrive
    /// while the row still shows its old content - a group about to expand. Falling back to the estimate there moved
    /// every row below to where the estimate put them, around a row that had not changed yet, until that render
    /// landed: on a phone, a gap that opened and closed again before the group grew.
    /// </summary>
    [Fact]
    public async Task InvalidateItemSize_RowOnScreen_KeepsTheSizeItIsRenderedAt()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var secondRowTransforms = await _fixture.Page.EvaluateAsync<string[]>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(100, {});
                list.rows.forEach((row, index) => row.setAttribute('data-key', `row-${index}`));
                const keys = Array.from({ length: 100 }, (_, index) => `row-${index}`);

                list.instance.invalidateItemSize('row-0');
                list.instance.setItems(keys.map((_, index) => index), keys, keys.map(() => 50), true);
                const beforeTheRowGrows = list.rows[1].style.transform;

                list.rows[0].style.height = '200px';
                await nextFrame();
                await nextFrame();
                const afterTheRowGrew = list.rows[1].style.transform;

                list.viewport.remove();
                return [beforeTheRowGrows, afterTheRowGrew];
            }");

        // Assert
        // Row 0 is rendered at 80px until its content changes; the estimate would have put row 1 at 50px.
        Assert.Equal(["translateY(80px)", "translateY(200px)"], secondRowTransforms);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>
    /// virtual-core answers a measurement without a ResizeObserver entry from its size cache, so measuring an
    /// invalidated row through it kept the size the row had before - wrong whenever the row had changed already.
    /// </summary>
    [Fact]
    public async Task InvalidateItemSize_RowAlreadyChanged_TakesItsCurrentSize()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var secondRowOffset = await _fixture.Page.EvaluateAsync<double>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(100, {});
                list.rows.forEach((row, index) => row.setAttribute('data-key', `row-${index}`));

                // Grown and invalidated in one task, before any ResizeObserver delivery.
                list.rows[0].style.height = '200px';
                list.instance.invalidateItemSize('row-0');
                const offset = list.instance.getOffsetForIndex(1, 'start');

                list.viewport.remove();
                return offset;
            }");

        // Assert
        // The size cached before the change would have put row 1 at 80px.
        Assert.Equal(200, secondRowOffset);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>
    /// With UseCachedMeasurements virtual-core never reads a row's size, so there is nothing to measure an invalidated
    /// row by: its size is dropped and the estimate stands in.
    /// </summary>
    [Fact]
    public async Task InvalidateItemSize_CachedMeasurements_FallsBackToTheEstimate()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var secondRowOffsets = await _fixture.Page.EvaluateAsync<double[]>(
            "async () => {" + _mountScript + @"
                const list = mount(100, { useCachedMeasurements: true });
                list.root.style.height = '2000px';
                await nextFrame();
                await nextFrame();
                const row = document.createElement('div');
                row.setAttribute('data-index', '0');
                row.setAttribute('data-key', 'row-0');
                row.style.height = '80px';
                list.root.appendChild(row);
                list.instance.resizeItem(0, 80);
                const beforeInvalidation = list.instance.getOffsetForIndex(1, 'start');

                list.instance.invalidateItemSize('row-0');
                const afterInvalidation = list.instance.getOffsetForIndex(1, 'start');

                list.viewport.remove();
                return [beforeInvalidation, afterInvalidation];
            }");

        // Assert
        // Kept, the cached 80px would never change again: in this mode a ResizeObserver delivery answers from it too.
        Assert.Equal([80, 50], secondRowOffsets);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>
    /// The item set reaches JS ahead of the render that shows it, so a row of the set it replaced can resize in
    /// between, while its index belongs to another item here. Dropping that measurement left the row at the size
    /// cached before it: virtual-core answers a measurement without a ResizeObserver entry from its size cache, so
    /// the measure pass after the render read the old size back, and the row overlapped the one below.
    /// </summary>
    [Fact]
    public async Task ResizeObserver_RowOfTheReplacedItemSet_KeepsItsNewSizeOnceRenderedAgain()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var sizes = await _fixture.Page.EvaluateAsync<string[]>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(100, {});
                list.rows.forEach((row, index) => row.setAttribute('data-key', `row-${index}`));

                // The set [row-0, row-2, row-1, ...] arrives ahead of its render, and row-1 - still drawn at
                // index 1, which this instance now holds for row-2 - grows.
                const keys = Array.from({ length: 100 }, (_, index) => `row-${index}`);
                [keys[1], keys[2]] = [keys[2], keys[1]];
                list.instance.setItems(keys.map((_, index) => index), keys, keys.map(() => 50), true);
                list.rows[1].style.height = '300px';
                await nextFrame();
                await nextFrame();

                // The render lands: row-2's element moves to index 1 and row-1's to index 2.
                list.rows[1].setAttribute('data-index', '2');
                list.rows[2].setAttribute('data-index', '1');
                list.root.insertBefore(list.rows[2], list.rows[1]);
                list.instance.measureRenderedRows();
                const sizesOfFirstRows = list.instance.takeSnapshot().slice(0, 3).map(x => `${x.key}=${x.size}`);

                list.viewport.remove();
                return sizesOfFirstRows;
            }");

        // Assert
        Assert.Equal(["row-0=80", "row-2=60", "row-1=300"], sizes);
        _fixture.AssertNoJsErrors();
    }

    /// <summary>
    /// .NET renders a new item set one interop message before setItems brings it to JS, and Blazor moves keyed rows
    /// to their new index meanwhile. A row that resizes in that window carries an index this instance still holds
    /// for another item, and recording its size there left an exercise row with a section header's height. The size
    /// belongs to the row's own key, where the render that shows the set reuses the element.
    /// </summary>
    [Fact]
    public async Task ResizeObserver_RowFromANewerItemSet_IsMeasuredOnlyUnderItsOwnKey()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var sizes = await _fixture.Page.EvaluateAsync<string[]>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(100, {});
                list.rows.forEach((row, index) => row.setAttribute('data-key', `row-${index}`));
                const sizesOfFirstRows = () => list.instance.takeSnapshot().slice(0, 3).map(x => `${x.key}=${x.size}`);

                // The set [row-0, row-2, row-1, ...] as .NET renders it: row-2's element moves to index 1,
                // which this instance still holds for row-1, and grows there.
                list.rows[1].setAttribute('data-index', '2');
                list.rows[2].setAttribute('data-index', '1');
                list.root.insertBefore(list.rows[2], list.rows[1]);
                list.rows[2].style.height = '300px';
                await nextFrame();
                await nextFrame();
                const beforeSetItems = sizesOfFirstRows();

                const keys = Array.from({ length: 100 }, (_, index) => `row-${index}`);
                [keys[1], keys[2]] = [keys[2], keys[1]];
                list.instance.setItems(keys.map((_, index) => index), keys, keys.map(() => 50), true);
                list.instance.measureRenderedRows();
                const afterSetItems = sizesOfFirstRows();

                list.viewport.remove();
                return [...beforeSetItems, ...afterSetItems];
            }");

        // Assert
        // Recorded under its own key straight away, and never under row-1, the key its index still has here.
        Assert.Equal(["row-0=80", "row-1=30", "row-2=300"], sizes[..3]);
        Assert.Equal(["row-0=80", "row-2=300", "row-1=30"], sizes[3..]);
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

    [Fact]
    public async Task SetItems_ReorderedSubsetThenTheFullListAgain_KeepsTheSizesMeasuredForKeysThatStayed()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var samples = await _fixture.Page.EvaluateAsync<ItemSetSample[]>(
            "async () => {" + _mountScript + @"
                // Twenty 50px items, row-i in slot i. Rows 0-5 are measured at real, distinct heights,
                // none of them 50, which virtual-core would not record as a change.
                const list = mount(20, {});
                const rows = [80, 30, 60, 40, 70, 20].map((height, index) => {
                    const row = document.createElement('div');
                    row.setAttribute('data-index', String(index));
                    row.style.height = `${height}px`;
                    list.root.appendChild(row);
                    return row;
                });
                list.instance.measureRenderedRows();
                rows.forEach(row => row.remove());

                const read = () => {
                    const measurements = list.instance.takeSnapshot();
                    return { keys: measurements.map(x => x.key), sizes: measurements.map(x => x.size) };
                };
                const samples = [];

                // Slots 4, 2 and 0, a key never sent before - slot 20 - then slots 10 to 12 as a run.
                list.instance.setItems([4, 2, 0, 20, 10, -2], ['fresh'], [33], false);
                samples.push(read());

                // Back to the list as it was first sent: one run over slots 0 to 19.
                list.instance.setItems([0, -19], [], [], false);
                samples.push(read());

                // The same set again, which .NET sends without an order.
                list.instance.setItems(null, [], [], false);
                samples.push(read());

                // A rebuilt table holds only what the rebuilding call sent.
                list.instance.setItems([1, 0], ['row-2', 'other'], [50, 45], true);
                samples.push(read());

                list.instance.dispose();
                list.viewport.remove();
                return samples;
            }");

        // Assert
        // The subset keeps the measured rows it contains and estimates the rest.
        Assert.Equal(["row-4", "row-2", "row-0", "fresh", "row-10", "row-11", "row-12"], samples[0].Keys);
        Assert.Equal([70, 60, 80, 33, 50, 50, 50], samples[0].Sizes);

        // Measurements survive per key, so rows 0, 2 and 4 come back measured. Rows 1, 3 and 5 left
        // the set in between, which evicted theirs.
        double[] fullListSizes = [80, 50, 60, 50, 70, .. Enumerable.Repeat(50d, 15)];
        Assert.Equal(Enumerable.Range(0, 20).Select(x => $"row-{x}"), samples[1].Keys);
        Assert.Equal(fullListSizes, samples[1].Sizes);

        Assert.Equal(samples[1].Keys, samples[2].Keys);
        Assert.Equal(fullListSizes, samples[2].Sizes);

        // The slot table is only how the set travels; row-2's measurement is held by key and survives it.
        Assert.Equal(["other", "row-2"], samples[3].Keys);
        Assert.Equal([45, 60], samples[3].Sizes);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task TakeSnapshot_RightAfterAMeasurementChanges_ReportsTheSizesTheVirtualizerUses()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var samples = await _fixture.Page.EvaluateAsync<double[][]>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                // With useAnimationFrameWithResizeObserver virtual-core measures a frame later,
                // after the observer below has already run.
                const list = await mountMeasured(20, { useAnimationFrameWithResizeObserver: false });
                const sizes = () => list.instance.takeSnapshot().slice(0, 3).map(x => x.size);

                // An observer created later on the same element is called straight after
                // virtual-core's, which has measured the row by then. The notification about that
                // measurement goes out a task later.
                const insideObserver = await new Promise(resolve => {
                    new ResizeObserver((_, observer) => {
                        observer.disconnect();
                        resolve(sizes());
                    }).observe(list.rows[1]);
                    list.rows[1].style.height = '90px';
                });
                await nextFrame();
                await nextFrame();

                // invalidateItemSize evicts a measurement without notifying at all.
                list.instance.invalidateItemSize('row-0');
                const afterInvalidation = sizes();

                list.instance.dispose();
                list.viewport.remove();
                return [insideObserver, afterInvalidation];
            }");

        // Assert
        // The snapshot exists to be restored on a later mount, which would then place rows by sizes
        // this list had already stopped using.
        Assert.Equal([80, 90, 60], samples[0]);
        Assert.Equal([50, 90, 60], samples[1]);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task GetOffsetForIndex_RightAfterARowAboveChanges_UsesTheSizesTheVirtualizerUses()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var offsets = await _fixture.Page.EvaluateAsync<double[]>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(20, { useAnimationFrameWithResizeObserver: false });
                const offsetOfRow3 = () => list.instance.getOffsetForIndex(3, 'start');

                const insideObserver = await new Promise(resolve => {
                    new ResizeObserver((_, observer) => {
                        observer.disconnect();
                        resolve(offsetOfRow3());
                    }).observe(list.rows[1]);
                    list.rows[1].style.height = '90px';
                });
                await nextFrame();
                await nextFrame();

                list.instance.invalidateItemSize('row-0');
                const afterInvalidation = offsetOfRow3();

                list.instance.dispose();
                list.viewport.remove();
                return [insideObserver, afterInvalidation];
            }");

        // Assert
        // Rows 0-2 at 80 + 90 + 60, then with row 0 back at its 50px estimate.
        Assert.Equal([230, 200], offsets);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task ScrollToIndex_RightAfterInvalidateItemSize_FirstWriteLandsOnTheTarget()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var scrollTops = await _fixture.Page.EvaluateAsync<double[]>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(40, {});

                // Evicting row 0's 80px measurement moves row 10 from 520 up to 490.
                list.instance.invalidateItemSize('row-0');
                list.instance.scrollToIndex(10, 'start', 'auto');
                const firstWrite = list.viewport.scrollTop;
                await nextFrame();
                await nextFrame();
                const settled = list.viewport.scrollTop;

                list.instance.dispose();
                list.viewport.remove();
                return [firstWrite, settled];
            }");

        // Assert
        // virtual-core re-aims a frame later anyway, so a first write at the old offset shows as a
        // jump of the evicted difference - in the toggle-a-group-then-scroll-to-it flow
        // InvalidateItemSize exists for.
        Assert.Equal([490, 490], scrollTops);
        _fixture.AssertNoJsErrors();
    }

    [Fact]
    public async Task GetSnapshot_RightAfterAMeasurementChanges_ReportsTheRangeTheVirtualizerUses()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var ranges = await _fixture.Page.EvaluateAsync<string[]>(
            "async () => {" + _mountScript + _measuredRowsScript + @"
                const list = await mountMeasured(20, { useAnimationFrameWithResizeObserver: false });
                const range = () => {
                    const snapshot = list.instance.getSnapshot();
                    return `${snapshot.startIndex}-${snapshot.endIndex}`;
                };

                // Rows of 80, 30, 60 and then 50px in the 200px viewport: rows 0 to 3 are visible.
                const measured = range();

                // Grown to 300px, row 0 fills the viewport alone. As in the TakeSnapshot test, this
                // observer runs straight after virtual-core's has measured it.
                const insideObserver = await new Promise(resolve => {
                    new ResizeObserver((_, observer) => {
                        observer.disconnect();
                        resolve(range());
                    }).observe(list.rows[0]);
                    list.rows[0].style.height = '300px';
                });
                await nextFrame();
                await nextFrame();

                // Back at its 50px estimate, row 0 leaves room for rows 0 to 4.
                list.instance.invalidateItemSize('row-0');
                const afterInvalidation = range();

                list.instance.dispose();
                list.viewport.remove();
                return [measured, insideObserver, afterInvalidation];
            }");

        // Assert
        // Joined, so a failure shows every sample rather than only the first that differs.
        Assert.Equal("0-3, 0-0, 0-4", string.Join(", ", ranges));
        _fixture.AssertNoJsErrors();
    }

    private sealed class ItemSetSample
    {
        [JsonPropertyName("keys")]
        public string[] Keys { get; set; } = [];

        [JsonPropertyName("sizes")]
        public double[] Sizes { get; set; } = [];
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
