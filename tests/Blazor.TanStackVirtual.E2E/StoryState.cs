using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace Blazor.TanStackVirtual.E2E;

/// <summary>
/// Reads the JSON a story's state panel renders.
/// </summary>
/// <remarks>
/// The panels are the only thing the browser suite can assert C# state against - what the host
/// actually received, rather than what the DOM happens to look like. They are read as
/// <see cref="JsonElement"/> rather than as a record per story, because the panels carry whatever
/// their own story is about.
/// </remarks>
internal static class StoryState
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    public static async Task<JsonElement> ReadAsync(ILocator canvas, string testId)
    {
        var json = await canvas.GetByTestId(testId).InnerTextAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Polls the panel until it satisfies <paramref name="predicate"/>, then returns it.
    /// </summary>
    /// <remarks>
    /// The last state observed is printed on failure. Almost every failure here is the slider doing
    /// something specific and wrong rather than the harness failing to see it, and the difference is
    /// only visible if the state is in the message.
    /// </remarks>
    public static async Task<JsonElement> WaitForAsync(
        ILocator canvas,
        string testId,
        Func<JsonElement, bool> predicate,
        string expectation)
    {
        var deadline = DateTime.UtcNow + _timeout;
        var lastState = default(JsonElement);

        while (DateTime.UtcNow < deadline)
        {
            lastState = await ReadAsync(canvas, testId);
            if (predicate(lastState))
            {
                return lastState;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out waiting for {expectation}. Last observed state: {lastState}");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>How many times the story recorded a named event.</summary>
    public static int EventCount(JsonElement state, string name)
    {
        return state.TryGetProperty("events", out var events) && events.TryGetProperty(name, out var count)
            ? count.GetInt32()
            : 0;
    }

    /// <summary>What a named event last carried, or empty if it never arrived.</summary>
    public static string EventValue(JsonElement state, string name)
    {
        return state.TryGetProperty("values", out var values) && values.TryGetProperty(name, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    public static int Int(JsonElement state, string name) => state.GetProperty(name).GetInt32();

    public static bool Bool(JsonElement state, string name) => state.GetProperty(name).GetBoolean();
}
