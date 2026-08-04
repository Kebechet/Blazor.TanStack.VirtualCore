using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Blazor.TanStack.VirtualCore.E2E;

[CollectionDefinition(Name)]
public sealed class DemoCollectionDefinition : ICollectionFixture<DemoFixture>
{
    public const string Name = "BlazingStory demo";
}

public sealed class DemoFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _serverOutput = new();
    private Process? _demoProcess;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = string.Empty;
    public IBrowser Browser { get; private set; } = null!;
    public IPage Page { get; private set; } = null!;

    /// <summary>
    /// Unhandled JavaScript errors and Blazor render exceptions seen since the last navigation.
    /// </summary>
    /// <remarks>
    /// A capture can produce plausible bytes while the interop threw on the way - a failed font
    /// fetch, an image that never resolved, a filter applied to a text node that has no classList.
    /// The panel would still report a valid PNG and the test would pass. Asserting the console is
    /// what makes those visible, and none of it is reachable from bUnit, which stubs the interop
    /// module out entirely.
    /// </remarks>
    private readonly List<string> _jsErrors = new();

    public async ValueTask InitializeAsync()
    {
        var repositoryRoot = RepositoryRoot;
        var port = ReserveTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "demo", "Blazor.TanStack.VirtualCore.Demo.csproj"));
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(BaseUrl);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        _demoProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _demoProcess.OutputDataReceived += CaptureServerOutput;
        _demoProcess.ErrorDataReceived += CaptureServerOutput;
        if (!_demoProcess.Start())
        {
            throw new InvalidOperationException("Could not start the BlazingStory demo process.");
        }

        _demoProcess.BeginOutputReadLine();
        _demoProcess.BeginErrorReadLine();
        await WaitForDemoAsync(TimeSpan.FromMinutes(5));

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            // The system Chrome rather than Playwright's bundled Chromium, so neither CI nor a
            // fresh clone needs a browser download step.
            Channel = "chrome",
            Headless = true,
            Args = ["--disable-dev-shm-usage"]
        });

        Page = await Browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 1000 }
        });

        Page.Console += (_, message) =>
        {
            if (message.Type == "error" && !IsUnrelatedNoise(message.Text))
            {
                lock (_jsErrors) _jsErrors.Add(FirstLine(message.Text));
            }
        };
        Page.PageError += (_, error) =>
        {
            lock (_jsErrors) _jsErrors.Add(FirstLine(error));
        };
    }

    /// <summary>
    /// Navigates straight to a story canvas, then waits until the virtualizer has actually placed
    /// rows.
    /// </summary>
    /// <remarks>
    /// The canvas page (<c>iframe.html</c>) is what BlazingStory's shell hosts in an iframe anyway,
    /// so addressing it directly is faster and steadier - no sidebar, no panels, no cross-frame hop.
    /// <para>
    /// Blazor paints the empty container before <c>OnAfterRenderAsync</c> has imported the interop
    /// module and constructed the virtualizer, so waiting for Blazor's first paint would race. The
    /// real signal is <c>data-index</c> elements existing, which only happens once placements have
    /// come back over interop.
    /// </para>
    /// </remarks>
    public async Task<ILocator> NavigateToStoryAsync(string storyId)
    {
        lock (_jsErrors) _jsErrors.Clear();
        var url = $"{BaseUrl}/iframe.html?viewMode=story&id={storyId}&e2e={Guid.NewGuid():N}";
        await Page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('[data-index]').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 60_000 });

        return Page.Locator("body");
    }

    /// <summary>
    /// The items currently in the DOM, ordered by index, with the placement the browser resolved.
    /// </summary>
    /// <remarks>
    /// Read from the live DOM rather than from a state panel on purpose: how many elements actually
    /// exist, and where they actually sit, is the entire claim virtualization makes. A panel could
    /// only report what C# believes.
    /// </remarks>
    public async Task<IReadOnlyList<RenderedRow>> ReadRenderedRowsAsync()
    {
        var rows = await Page.EvaluateAsync<RenderedRow[]>(
            @"() => Array.from(document.querySelectorAll('[data-index]')).map(el => {
                const style = getComputedStyle(el);
                // getComputedStyle resolves translateX/Y to a matrix(a,b,c,d,tx,ty). Reading tx and
                // ty out of it - rather than parsing the style attribute - is what proves the
                // browser accepted the declaration instead of merely that a string was written.
                let translateX = 0, translateY = 0;
                if (style.transform && style.transform !== 'none') {
                    const parts = style.transform.slice(style.transform.indexOf('(') + 1, -1).split(',').map(parseFloat);
                    if (parts.length >= 6) { translateX = parts[4]; translateY = parts[5]; }
                }
                const rect = el.getBoundingClientRect();
                return {
                    index: parseInt(el.getAttribute('data-index'), 10),
                    translateX: translateX,
                    translateY: translateY,
                    width: rect.width,
                    height: rect.height,
                    // The component writes the cross-axis position inline; a multi-lane layout is
                    // the only thing that makes this differ between items.
                    crossStart: (el.style.left || el.style.top || '')
                };
            }).sort((a, b) => a.index - b.index)");

        return rows;
    }

    /// <summary>Scrolls the story's viewport and waits for the rendered window to actually move.</summary>
    public async Task ScrollViewportAsync(string testId, int deltaY)
    {
        var before = await ReadRenderedRowsAsync();
        var firstBefore = before.Count > 0 ? before[0].Index : -1;

        await Page.EvalOnSelectorAsync(
            $"[data-testid=\"{testId}-viewport\"]",
            "(el, delta) => el.scrollTop += delta",
            deltaY);

        await Page.WaitForFunctionAsync(
            @"first => {
                const els = document.querySelectorAll('[data-index]');
                if (els.length === 0) return false;
                const indexes = Array.from(els).map(el => parseInt(el.getAttribute('data-index'), 10));
                return Math.min(...indexes) !== first;
            }",
            firstBefore,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    /// <summary>Fails the current test if the page logged an unhandled JavaScript or Blazor render error.</summary>
    public void AssertNoJsErrors()
    {
        string[] errors;
        lock (_jsErrors) errors = _jsErrors.Distinct().ToArray();
        Assert.True(
            errors.Length == 0,
            "The page reported unhandled errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static string FirstLine(string text)
    {
        var lines = text.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? text : lines[0];
    }

    /// <summary>Browser-extension chatter and asset noise that says nothing about this library.</summary>
    private static bool IsUnrelatedNoise(string text)
    {
        return text.Contains("contentscript")
            || text.Contains("Failed to load resource")
            || text.Contains("preloaded using link preload");
    }

    public async ValueTask DisposeAsync()
    {
        if (Page is not null)
        {
            await Page.CloseAsync();
        }

        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();

        if (_demoProcess is { HasExited: false })
        {
            _demoProcess.Kill(entireProcessTree: true);
            await _demoProcess.WaitForExitAsync();
        }

        _demoProcess?.Dispose();
    }

    private async Task WaitForDemoAsync(TimeSpan timeout)
    {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_demoProcess?.HasExited == true)
            {
                throw new InvalidOperationException($"The demo exited before becoming ready.{Environment.NewLine}{ServerLog()}");
            }

            try
            {
                using var response = await client.GetAsync(BaseUrl);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"The demo was not ready after {timeout}.{Environment.NewLine}{ServerLog()}");
    }

    private void CaptureServerOutput(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            _serverOutput.Enqueue(args.Data);
        }
    }

    private string ServerLog()
    {
        return string.Join(Environment.NewLine, _serverOutput.TakeLast(100));
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    public static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "demo", "Blazor.TanStack.VirtualCore.Demo.csproj")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "Blazor.TanStack.VirtualCore.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Blazor.TanStack.VirtualCore repository root.");
    }
}
