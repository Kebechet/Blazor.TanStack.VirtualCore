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
            @"async () => {
                const moduleUrl = new URL('_content/Kebechet.Blazor.TanStack.VirtualCore/adaptive-virtualize-interop.js', document.baseURI);
                const { create } = await import(moduleUrl.href);
                const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));

                const viewport = document.createElement('div');
                viewport.style.cssText = 'overflow:auto;width:300px;height:200px;';
                const root = document.createElement('div');
                viewport.appendChild(root);
                document.body.appendChild(viewport);

                let notifications = 0;
                const dotNetRef = { invokeMethodAsync: () => { notifications++; return Promise.resolve(); } };
                const keys = Array.from({ length: 100 }, (_, index) => `row-${index}`);
                const instance = create(root, dotNetRef, keys, keys.map(() => 50), 50, { useAnimationFrameWithResizeObserver: true });
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
                        instance.dispose();
                        notificationsAtDispose = notifications;
                        resolve();
                    }).observe(viewport);
                    viewport.style.height = '400px';
                });
                await nextFrame();
                await nextFrame();

                viewport.remove();
                return notifications - notificationsAtDispose;
            }");

        // Assert
        // In the app that notification targets a DotNetObjectReference released right after dispose,
        // and Blazor rejects it in the browser console.
        Assert.Equal(0, notificationsAfterDispose);
        _fixture.AssertNoJsErrors();
    }
}
