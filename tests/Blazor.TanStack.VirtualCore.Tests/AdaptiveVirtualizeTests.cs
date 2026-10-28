using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Shouldly;
using Xunit;
using TestContext = Bunit.TestContext;

namespace Kebechet.Blazor.TanStack.VirtualCore.Tests;

public class AdaptiveVirtualizeTests
{
	private const string _modulePath = "./_content/Kebechet.Blazor.TanStack.VirtualCore/adaptive-virtualize-interop.js";

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

	[Fact]
	public async Task DisposeAsync_JSObjectAlreadyGoneFromThePage_DoesNotThrow()
	{
		// Arrange
		using var context = CreateContext();
		var module = context.JSInterop.SetupModule(_modulePath);
		var instance = module.SetupModule(invocation => invocation.Identifier == "create");
		instance.SetupVoid("dispose").SetException(new JSException("JS object instance with ID 20 does not exist (has it been disposed?)"));
		var component = RenderWith(context, ["a"]);

		// Act
		var dispose = () => component.InvokeAsync(async () => await component.Instance.DisposeAsync());

		// Assert
		await Should.NotThrowAsync(dispose);
		instance.VerifyInvoke("dispose");
	}

	[Theory]
	[InlineData("setOptions")]
	[InlineData("setItems")]
	[InlineData("measureRenderedRows")]
	public async Task DisposeAsync_WhileARenderAwaitsTheJSInstance_ThatRenderGoesNoFurther(string callInFlight)
	{
		// Arrange
		using var context = CreateContext();
		var module = context.JSInterop.SetupModule(_modulePath);
		var instance = module.SetupModule(invocation => invocation.Identifier == "create");
		var heldCall = instance.SetupVoid(callInFlight, _ => true);
		var jsDispose = instance.SetupVoid("dispose");
		var component = RenderWith(context, ["a"]);
		var layoutMeasuredCount = 0;

		// New options and new items in one render: OnAfterRenderAsync awaits setOptions, setItems and
		// measureRenderedRows in turn, then calls OnLayoutMeasured. The call under test stays in
		// flight; the others answer at once.
		component.SetParametersAndRender(parameters => parameters
			.Add(p => p.Options, new VirtualizeOptions())
			.Add(p => p.Items, (IReadOnlyList<string>) ["a", "b"])
			.Add(p => p.OnLayoutMeasured, (Func<Task>) (() =>
			{
				layoutMeasuredCount++;
				return Task.CompletedTask;
			})));

		// Act
		var disposal = component.InvokeAsync(async () => await component.Instance.DisposeAsync());
		heldCall.SetVoidResult();
		// The render's continuation was queued on the renderer's dispatcher by the line above, so a
		// no-op behind it only completes once that continuation has run.
		await component.InvokeAsync(() => { });
		jsDispose.SetVoidResult();
		await disposal;

		// Assert
		// Anything after the call in flight would go to an instance disposal is releasing. Depending on
		// how far disposal has got, that either reaches the instance after its dispose call or throws
		// on the render path - which the renderer reports, so both are checked.
		string[] renderCalls = ["setOptions", "setItems", "measureRenderedRows"];
		foreach (var laterCall in renderCalls.SkipWhile(call => call != callInFlight).Skip(1))
		{
			instance.VerifyNotInvoke(laterCall);
		}

		layoutMeasuredCount.ShouldBe(0);
		context.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
	}

	[Fact]
	public async Task DisposeAsync_WhileTheModuleIsBeingImported_ReleasesItAndNeverCreatesAnInstance()
	{
		// Arrange
		using var context = CreateContext();
		var module = new FakeJS();
		var runtime = new FakeJS().Returning("import", module);
		runtime.Hold("import");
		context.Services.AddSingleton<IJSRuntime>(runtime);
		var component = RenderWith(context, ["a"]);

		// Act
		await component.InvokeAsync(async () => await component.Instance.DisposeAsync());
		runtime.Answer("import");
		await component.InvokeAsync(() => { });

		// Assert
		module.Calls.ShouldBeEmpty();
		module.IsReleased.ShouldBeTrue();
	}

	[Fact]
	public async Task DisposeAsync_WhileTheJSInstanceIsBeingCreated_ReleasesItOnceItArrives()
	{
		// Arrange
		using var context = CreateContext();
		var instance = new FakeJS();
		var module = new FakeJS().Returning("create", instance);
		module.Hold("create");
		context.Services.AddSingleton<IJSRuntime>(new FakeJS().Returning("import", module));
		var component = RenderWith(context, ["a"]);
		var selfRef = (DotNetObjectReference<AdaptiveVirtualize<string>>) module.Calls.Single().Args![1]!;

		// Act
		await component.InvokeAsync(async () => await component.Instance.DisposeAsync());
		// The instance on its way was constructed with this reference and can call it the moment it
		// exists, so disposal must not have released it yet.
		Should.NotThrow(() => selfRef.Value);
		module.Answer("create");
		await component.InvokeAsync(() => { });

		// Assert
		// Left unreleased, the instance keeps its scroll and resize observers attached for the life
		// of the page.
		instance.Calls.Select(call => call.Identifier).ShouldBe(["dispose"]);
		instance.IsReleased.ShouldBeTrue();
		module.IsReleased.ShouldBeTrue();
		Should.Throw<ObjectDisposedException>(() => selfRef.Value);
	}

	[Theory]
	[InlineData(typeof(TaskCanceledException))]
	[InlineData(typeof(JSException))]
	public async Task DisposeAsync_WhileTheJSInstanceIsBeingCreated_StillReleasesEverythingIfCreationFails(Type failure)
	{
		// Arrange
		using var context = CreateContext();
		var module = new FakeJS().Throwing("create", (Exception) Activator.CreateInstance(failure, "create failed")!);
		module.Hold("create");
		context.Services.AddSingleton<IJSRuntime>(new FakeJS().Returning("import", module));
		var component = RenderWith(context, ["a"]);
		var selfRef = (DotNetObjectReference<AdaptiveVirtualize<string>>) module.Calls.Single().Args![1]!;

		// Act
		await component.InvokeAsync(async () => await component.Instance.DisposeAsync());
		module.Answer("create");
		await component.InvokeAsync(() => { });

		// Assert
		// The JS runtime tracks the reference from the moment create was sent, so leaving it
		// unreleased pins the disposed component for as long as the runtime lives.
		Should.Throw<ObjectDisposedException>(() => selfRef.Value);
		module.IsReleased.ShouldBeTrue();
		// With the component gone the failure has nobody left to tell; letting it escape would only
		// surface as an unhandled render exception.
		context.Renderer.UnhandledException.IsCompleted.ShouldBeFalse();
	}

	[Fact]
	public async Task DisposeAsync_InteropCallsTimeOut_StillRunsTheRestOfTheCleanup()
	{
		// Arrange
		// What Blazor Server does to each call when the browser has stopped answering.
		var instance = new FakeJS().Throwing("dispose", new TaskCanceledException());
		var module = new FakeJS()
			.Returning("create", instance)
			.Throwing(nameof(IAsyncDisposable.DisposeAsync), new TaskCanceledException());
		using var context = CreateContext();
		context.Services.AddSingleton<IJSRuntime>(new FakeJS().Returning("import", module));
		var component = RenderWith(context, ["a"]);
		var selfRef = (DotNetObjectReference<AdaptiveVirtualize<string>>) module.Calls.Single().Args![1]!;

		// Act
		// Awaited directly: Should.NotThrowAsync lets a canceled task through as "did not throw".
		await component.InvokeAsync(async () => await component.Instance.DisposeAsync());

		// Assert
		Should.Throw<ObjectDisposedException>(() => selfRef.Value);
	}

	/// <summary>
	/// Stands in for the runtime, the module and the instance alike, whose call surfaces are
	/// identical. Hand-rolled because bUnit's module stand-ins always answer at once, and these tests
	/// need a call still in flight when the component is disposed.
	/// </summary>
	private sealed class FakeJS : IJSRuntime, IJSObjectReference
	{
		private readonly Dictionary<string, object?> _results = [];
		private readonly Dictionary<string, Exception> _failures = [];
		private readonly Dictionary<string, TaskCompletionSource> _heldCalls = [];

		public List<(string Identifier, object?[]? Args)> Calls { get; } = [];

		public bool IsReleased { get; private set; }

		public FakeJS Returning(string identifier, object? result)
		{
			_results[identifier] = result;
			return this;
		}

		/// <summary>Fails calls to <paramref name="identifier"/>; pass <c>DisposeAsync</c> to fail the release.</summary>
		public FakeJS Throwing(string identifier, Exception exception)
		{
			_failures[identifier] = exception;
			return this;
		}

		/// <summary>Leaves calls to <paramref name="identifier"/> unanswered until <see cref="Answer"/>.</summary>
		public void Hold(string identifier)
		{
			_heldCalls[identifier] = new TaskCompletionSource();
		}

		public void Answer(string identifier)
		{
			_heldCalls[identifier].SetResult();
		}

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
		{
			return InvokeAsync<TValue>(identifier, CancellationToken.None, args);
		}

		public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
		{
			Calls.Add((identifier, args));
			if (_heldCalls.TryGetValue(identifier, out var heldCall))
			{
				// Resumes on the thread calling Answer rather than queueing behind the renderer, so the
				// component's own continuation is already queued by the time Answer returns.
				await heldCall.Task.ConfigureAwait(false);
			}

			if (_failures.TryGetValue(identifier, out var failure))
			{
				throw failure;
			}

			return (TValue) _results.GetValueOrDefault(identifier)!;
		}

		public ValueTask DisposeAsync()
		{
			if (_failures.TryGetValue(nameof(DisposeAsync), out var failure))
			{
				return ValueTask.FromException(failure);
			}

			IsReleased = true;
			return ValueTask.CompletedTask;
		}
	}
}
