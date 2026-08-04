namespace Kebechet.Blazor.TanStack.VirtualCore;

/// <summary>Where the target row should end up within the viewport.</summary>
public enum ScrollAlignment : byte
{
	/// <summary>Scrolls the minimum distance needed to bring the row fully into view.</summary>
	Auto,
	/// <summary>Aligns the row's top edge with the top of the viewport.</summary>
	Start,
	/// <summary>Centres the row in the viewport.</summary>
	Center,
	/// <summary>Aligns the row's bottom edge with the bottom of the viewport.</summary>
	End
}

/// <summary>Whether the scroll is animated.</summary>
public enum ScrollBehavior : byte
{
	/// <summary>Lets the browser decide, honouring the user's reduced-motion preference.</summary>
	Auto,
	/// <summary>Animates to the target position.</summary>
	Smooth,
	/// <summary>Jumps to the target position with no animation.</summary>
	Instant
}

internal static class ScrollOptionExtensions
{
	/// <summary>
	/// These strings are the virtualizer's own option values, not merely lowercased enum names -
	/// deriving them with <c>nameof</c> would let an enum rename silently change what is sent.
	/// </summary>
	internal static string ToJsName(this ScrollAlignment alignment)
	{
		return alignment switch
		{
			ScrollAlignment.Auto => "auto",
			ScrollAlignment.Start => "start",
			ScrollAlignment.Center => "center",
			ScrollAlignment.End => "end",
			_ => throw new NotImplementedException($"Unhandled {nameof(ScrollAlignment)} value: {alignment}")
		};
	}

	/// <inheritdoc cref="ToJsName(ScrollAlignment)"/>
	internal static string ToJsName(this ScrollBehavior behavior)
	{
		return behavior switch
		{
			ScrollBehavior.Auto => "auto",
			ScrollBehavior.Smooth => "smooth",
			ScrollBehavior.Instant => "instant",
			_ => throw new NotImplementedException($"Unhandled {nameof(ScrollBehavior)} value: {behavior}")
		};
	}
}
