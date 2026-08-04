using System.Text.Json.Serialization;

namespace Blazor.TanStackVirtual.E2E;

/// <summary>
/// One row as the browser actually rendered it. <see cref="Offset"/> is read back from the computed
/// transform matrix rather than the style attribute, so it reflects a value the browser accepted.
/// </summary>
public sealed class RenderedRow
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("offset")]
    public double Offset { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}
