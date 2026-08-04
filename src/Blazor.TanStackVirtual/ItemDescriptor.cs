namespace Kebechet.Blazor.TanStackVirtual;

/// <summary>
/// The per-row facts the JS side needs to answer the virtualizer's <c>getItemKey</c> and
/// <c>estimateSize</c> callbacks synchronously, without calling back into .NET on every scroll
/// frame. Sent once per item-set change.
/// </summary>
public sealed class ItemDescriptor
{
	/// <summary>Stable identity, also the virtualizer's measurement-cache key.</summary>
	public string Key { get; set; } = string.Empty;

	/// <summary>Expected pixel height, used until the row has actually been measured.</summary>
	public double EstimatedSize { get; set; }
}
