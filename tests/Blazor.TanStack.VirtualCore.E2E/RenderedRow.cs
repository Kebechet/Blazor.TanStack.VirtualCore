using System.Text.Json.Serialization;

namespace Blazor.TanStack.VirtualCore.E2E;

/// <summary>
/// One item as the browser actually rendered it. Offsets are read back from the computed transform
/// matrix rather than the style attribute, so they reflect a value the browser accepted.
/// </summary>
public sealed class RenderedRow
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Translation along the x axis, in pixels. Non-zero only for a horizontal list.</summary>
    [JsonPropertyName("translateX")]
    public double TranslateX { get; set; }

    /// <summary>Translation along the y axis, in pixels. Non-zero only for a vertical list.</summary>
    [JsonPropertyName("translateY")]
    public double TranslateY { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    /// <summary>The inline cross-axis position the component computed, e.g. <c>33.3333%</c> in a three-lane grid.</summary>
    [JsonPropertyName("crossStart")]
    public string CrossStart { get; set; } = string.Empty;

    /// <summary>Offset along whichever axis the list scrolls, so tests can be axis-agnostic.</summary>
    public double Offset(bool isHorizontal)
    {
        return isHorizontal ? TranslateX : TranslateY;
    }

    /// <summary>Size along whichever axis the list scrolls.</summary>
    public double Size(bool isHorizontal)
    {
        return isHorizontal ? Width : Height;
    }
}
