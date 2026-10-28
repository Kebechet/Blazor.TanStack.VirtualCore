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
    /// 300x200 viewport, with a .NET stand-in that counts the notifications it receives.
    /// </summary>
    private const string _mountScript = @"
        const moduleUrl = new URL('_content/Kebechet.Blazor.TanStack.VirtualCore/adaptive-virtualize-interop.js', document.baseURI);
        const { create } = await import(moduleUrl.href);
        const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));
        const mount = (itemCount, options) => {
            const viewport = document.createElement('div');
            viewport.style.cssText = 'overflow:auto;width:300px;height:200px;';
            const root = document.createElement('div');
            viewport.appendChild(root);
            document.body.appendChild(viewport);

            const list = { viewport, root, notifications: 0 };
            const dotNetRef = { invokeMethodAsync: () => { list.notifications++; return Promise.resolve(); } };
            const keys = Array.from({ length: itemCount }, (_, index) => `row-${index}`);
            list.instance = create(root, dotNetRef, keys, keys.map(() => 50), 50, options);
            return list;
        };";

    private readonly DemoFixture _fixture;

    public InteropModuleTests(DemoFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dispose_WithAResizeFrameAlreadyQueued_NeverNotifiesDotNetAgain()
    {
        // Arrange
        await _fixture.NavigateToStoryAsync(_story);

        // Act
        var notificationsAfterDispose = await _fixture.Page.EvaluateAsync<int>(
            "async () => {" + _mountScript + @"
                const list = mount(100, { useAnimationFrameWithResizeObserver: true });
                await nextFrame();
                await nextFrame();

                // virtual-core's ResizeObserver defers its work to the next animation frame, which
                // its teardown has to cancel - 3.17.7 did not, 3.19.4 onwards does. An observer
                // created later on the same element is called straight after virtual-core's, so
                // disposing from it lands between the frame being queued and it running.
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
            }");

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
