using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;
using TestContext = Bunit.TestContext;

namespace Kebechet.Blazor.TanStackVirtual.Tests;

public class AdaptiveVirtualizeTests
{
	private static TestContext CreateContext()
	{
		var context = new TestContext();
		// The component imports its interop module and calls into it on first render. None of that
		// is under test here, so every call is stubbed.
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		return context;
	}

	private static IRenderedComponent<AdaptiveVirtualize<string>> RenderWith(TestContext context, IReadOnlyList<string> items)
	{
		return context.RenderComponent<AdaptiveVirtualize<string>>(parameters => parameters
			.Add(p => p.Items, items)
			.Add(p => p.ItemKeyExtractor, (Func<string, string>) (x => x))
			.Add(p => p.ItemContent, (RenderFragment<string>) (item => builder =>
			{
				builder.OpenElement(0, "span");
				builder.AddContent(1, item);
				builder.CloseElement();
			})));
	}

	[Fact]
	public void Render_CommaDecimalCulture_StillEmitsDotSeparatedPixels()
	{
		// Arrange
		// cs-CZ uses a comma as the decimal separator. Formatting with the current culture would
		// emit translateY(12,5px), which the browser discards as an invalid declaration - every row
		// then renders at offset zero, stacked on the first.
		var originalCulture = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");
		try
		{
			using var context = CreateContext();
			var component = RenderWith(context, ["a", "b"]);

			// Act
			component.InvokeAsync(() => component.Instance.OnVirtualItemsChanged(
				[
					new VirtualItem { Index = 0, Start = 0, Size = 12.5, End = 12.5, Key = "a" },
					new VirtualItem { Index = 1, Start = 12.5, Size = 12.5, End = 25, Key = "b" },
				],
				totalSize: 25.5)).Wait();

			// Assert
			var markup = component.Markup;
			markup.ShouldContain("translateY(12.5px)");
			markup.ShouldContain("height:25.5px");
			markup.ShouldNotContain(",5px");
		}
		finally
		{
			CultureInfo.CurrentCulture = originalCulture;
		}
	}

	[Fact]
	public void Render_KeyMismatchBetweenPlacementAndItem_SkipsThatRow()
	{
		// Arrange
		using var context = CreateContext();
		var component = RenderWith(context, ["a", "b"]);

		// Act
		// The placement for index 1 carries a key that no longer matches the item there - the item
		// set moved under it, so its offset is stale.
		component.InvokeAsync(() => component.Instance.OnVirtualItemsChanged(
			[
				new VirtualItem { Index = 0, Start = 0, Size = 10, End = 10, Key = "a" },
				new VirtualItem { Index = 1, Start = 10, Size = 10, End = 20, Key = "stale-key" },
			],
			totalSize: 20)).Wait();

		// Assert
		// Rendering the stale placement would visibly misplace the row until the next JS round-trip.
		component.Markup.ShouldContain(">a<");
		component.Markup.ShouldNotContain(">b<");
	}

	[Fact]
	public void Render_PlacementIndexOutsideItems_IsIgnoredInsteadOfThrowing()
	{
		// Arrange
		using var context = CreateContext();
		var component = RenderWith(context, ["a"]);

		// Act
		// Placements can arrive for an item set that has already shrunk, because the JS round-trip
		// is asynchronous. Indexing straight into Items here would throw during render.
		var render = () => component.InvokeAsync(() => component.Instance.OnVirtualItemsChanged(
			[
				new VirtualItem { Index = 0, Start = 0, Size = 10, End = 10, Key = "a" },
				new VirtualItem { Index = 7, Start = 70, Size = 10, End = 80, Key = "gone" },
			],
			totalSize: 80)).Wait();

		// Assert
		Should.NotThrow(render);
		component.Markup.ShouldContain(">a<");
	}

	[Fact]
	public void Render_BeforeAnyPlacementsArrive_RendersAnEmptySizedContainer()
	{
		// Arrange & Act
		using var context = CreateContext();
		var component = RenderWith(context, ["a", "b", "c"]);

		// Assert
		// Height stays 0 until the virtualizer reports a total, so the page must not reserve space
		// it cannot yet justify.
		component.Markup.ShouldContain("height:0px");
	}
}
