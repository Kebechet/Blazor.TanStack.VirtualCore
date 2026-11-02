using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Shouldly;
using Xunit;
using TestContext = Bunit.TestContext;

namespace Kebechet.Blazor.TanStack.VirtualCore.Tests;

/// <summary>
/// What an item-set change sends across interop. Every call is serialized by Blazor's own
/// <see cref="JSRuntime"/>, so the payloads asserted on are the bytes a Blazor Hybrid app pushes
/// through its WebView bridge, and <see cref="JsItemSet"/> replays them the way the interop module
/// does to check the JS side still ends up with exactly the item set the component holds.
/// </summary>
public class ItemSetInteropTests
{
	private const double _estimatedItemSize = 50;

	private static readonly string[] _rowKeys = Enumerable.Range(0, 100)
		.Select(x => $"row-{x}")
		.ToArray();

	[Fact]
	public void ItemsChange_NewListWithTheSameContent_SendsNoKeys()
	{
		// Arrange
		using var context = new TestContext();
		var recorder = Register(context);
		var component = RenderWith(context, recorder, _rowKeys);

		// Act
		SetItems(component, recorder, _rowKeys.ToArray());

		// Assert
		var payload = recorder.LastArguments("setItems");
		payload.ShouldNotContain("\"row-");
		payload.Length.ShouldBeLessThan(40);
		JsItemSet.Replay(recorder).Keys.ShouldBe(_rowKeys);
	}

	[Fact]
	public void ItemsChange_ReorderedSubset_SendsNoKeysTheJSSideAlreadyHolds()
	{
		// Arrange
		using var context = new TestContext();
		var recorder = Register(context);
		var component = RenderWith(context, recorder, _rowKeys);
		// What a relevance-ranked search returns: some of the items, not in list order.
		var subset = _rowKeys
			.Where((_, index) => index % 3 == 0)
			.Reverse()
			.ToArray();

		// Act
		SetItems(component, recorder, subset);

		// Assert
		recorder.LastArguments("setItems").ShouldNotContain("\"row-");
		JsItemSet.Replay(recorder).Keys.ShouldBe(subset);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ItemsChange_BackToAnEarlierList_SendsNoKeys(bool isSameInstance)
	{
		// Arrange
		using var context = new TestContext();
		var recorder = Register(context);
		var component = RenderWith(context, recorder, _rowKeys);
		SetItems(component, recorder, _rowKeys.Take(10).Reverse().ToArray());

		// Act
		// Clearing a search: the app hands back either the list it started from or a copy of it.
		SetItems(component, recorder, isSameInstance ? _rowKeys : _rowKeys.ToArray());

		// Assert
		var payload = recorder.LastArguments("setItems");
		payload.ShouldNotContain("\"row-");
		payload.Length.ShouldBeLessThan(40);
		JsItemSet.Replay(recorder).Keys.ShouldBe(_rowKeys);
	}

	[Fact]
	public void ItemsChange_KeysNeverSentBefore_SendsOnlyThoseKeys()
	{
		// Arrange
		using var context = new TestContext();
		var recorder = Register(context);
		var component = RenderWith(context, recorder, _rowKeys);
		string[] items = ["row-7", "new-a", "row-3", "row-4", "new-b"];

		// Act
		SetItems(component, recorder, items);

		// Assert
		var payload = recorder.LastArguments("setItems");
		payload.ShouldContain("\"new-a\"");
		payload.ShouldContain("\"new-b\"");
		payload.ShouldNotContain("\"row-");
		JsItemSet.Replay(recorder).Keys.ShouldBe(items);
	}

	[Fact]
	public void ItemsChange_EstimateChangesForAKnownKey_SendsThatItemAloneWithItsNewEstimate()
	{
		// Arrange
		using var context = new TestContext();
		var recorder = Register(context);
		var estimates = _rowKeys.ToDictionary(x => x, _ => 60d);
		var component = RenderWith(context, recorder, _rowKeys, x => estimates[x]);

		// Act
		// A group row expanding: same key, a taller estimate.
		estimates["row-5"] = 180;
		SetItems(component, recorder, _rowKeys.ToArray());

		// Assert
		var payload = recorder.LastArguments("setItems");
		payload.ShouldContain("\"row-5\"");
		payload.Replace("\"row-5\"", string.Empty).ShouldNotContain("\"row-");
		var jsItemSet = JsItemSet.Replay(recorder);
		jsItemSet.Keys.ShouldBe(_rowKeys);
		jsItemSet.Sizes.ShouldBe(_rowKeys.Select(x => estimates[x]));
	}

	[Fact]
	public void ItemsChange_SameKeyTwiceWithDifferentEstimates_TheJSSideKeepsBoth()
	{
		// Arrange
		// Duplicate keys break the component's contract, but each position still used to reach the
		// JS side with its own estimate, so it still does.
		using var context = new TestContext();
		var recorder = Register(context);
		var component = RenderWith(context, recorder, ["a", "b"], x => x.StartsWith("tall") ? 200 : 40);
		var items = new[] { "a", "tall-b", "b" };

		// Act
		component.SetParametersAndRender(parameters => parameters
			.Add(p => p.ItemKeyExtractor, (Func<string, string>) (x => x.Replace("tall-", string.Empty)))
			.Add(p => p.Items, items));
		component.WaitForAssertion(() => recorder.Count("setItems").ShouldBe(1));

		// Assert
		var jsItemSet = JsItemSet.Replay(recorder);
		jsItemSet.Keys.ShouldBe(["a", "b", "b"]);
		jsItemSet.Sizes.ShouldBe([40, 200, 40]);
	}

	[Fact]
	public void ItemsChange_KeysKeepChanging_TheJSSideTableStaysBounded()
	{
		// Arrange
		// A rotating window - a live feed - where every set is ten keys never seen before.
		const int itemCount = 10;
		using var context = new TestContext();
		var recorder = Register(context);
		var component = RenderWith(context, recorder, Window(0));

		// Act
		for (var round = 1; round <= 50; round++)
		{
			SetItems(component, recorder, Window(round));
		}

		// Assert
		// Every key ever sent staying in the table would hold 510 by now.
		var jsItemSet = JsItemSet.Replay(recorder);
		jsItemSet.LargestTableSize.ShouldBeLessThanOrEqualTo(2 * itemCount);
		jsItemSet.Keys.ShouldBe(Window(50));

		static string[] Window(int round)
		{
			return Enumerable.Range(round * itemCount, itemCount)
				.Select(x => $"feed-{x}")
				.ToArray();
		}
	}

	[Fact]
	public void ItemsChange_AfterASetItemsThatFailed_ResendsTheWholeItemSet()
	{
		// Arrange
		using var context = new TestContext();
		var recorder = Register(context);
		var component = RenderWith(context, recorder, _rowKeys);
		recorder.FailNext("setItems");
		// A failure while the component is alive still throws, as it always has.
		var failure = Should.Throw<JSException>(() => component.SetParametersAndRender(parameters => parameters.Add(p => p.Items, _rowKeys.Take(50).ToArray())));

		// Act
		var items = _rowKeys.Skip(50).Append("new-a").ToArray();
		// bUnit reports that failure once more from the next render, after the render has run.
		var exception = Record.Exception(() => component.SetParametersAndRender(parameters => parameters.Add(p => p.Items, items)));

		// Assert
		(exception is null || ReferenceEquals(exception, failure)).ShouldBeTrue();
		recorder.FailedCount.ShouldBe(1);
		recorder.Count("setItems").ShouldBe(1);
		// The failed call may never have reached the JS side, so nothing it added can be referred to.
		recorder.LastArguments("setItems").ShouldContain("\"row-99\"");
		JsItemSet.Replay(recorder).Keys.ShouldBe(items);
	}

	private static InteropRecorder Register(TestContext context)
	{
		var recorder = new InteropRecorder();
		context.Services.AddSingleton<IJSRuntime>(recorder);
		return recorder;
	}

	private static IRenderedComponent<AdaptiveVirtualize<string>> RenderWith(
		TestContext context,
		InteropRecorder recorder,
		IReadOnlyList<string> items,
		Func<string, double>? itemSizeEstimator = null)
	{
		var component = context.RenderComponent<AdaptiveVirtualize<string>>(parameters => parameters
			.Add(p => p.Items, items)
			.Add(p => p.ItemKeyExtractor, (Func<string, string>) (x => x))
			.Add(p => p.ItemSizeEstimator, itemSizeEstimator)
			.Add(p => p.EstimatedItemSize, _estimatedItemSize)
			.Add(p => p.ItemContent, (RenderFragment<string>) (item => builder => builder.AddContent(0, item))));

		// The instance is created from an awaited import, after the first render has returned.
		component.WaitForAssertion(() => recorder.Count("create").ShouldBe(1));
		return component;
	}

	private static void SetItems(IRenderedComponent<AdaptiveVirtualize<string>> component, InteropRecorder recorder, IReadOnlyList<string> items)
	{
		var setItemsCount = recorder.Count("setItems");
		component.SetParametersAndRender(parameters => parameters.Add(p => p.Items, items));
		component.WaitForAssertion(() => recorder.Count("setItems").ShouldBe(setItemsCount + 1));
	}

	/// <summary>
	/// A real <see cref="JSRuntime"/> with the browser replaced: it records the JSON of every call,
	/// answers it at once, and hands out object references for <c>import</c> and <c>create</c>.
	/// </summary>
	private sealed class InteropRecorder : JSRuntime
	{
		private readonly List<(string Identifier, string ArgumentsJson)> _calls = [];
		private readonly HashSet<string> _failingIdentifiers = [];
		private long _nextObjectId;

		public int FailedCount { get; private set; }

		public IReadOnlyList<(string Identifier, string ArgumentsJson)> Calls
		{
			get
			{
				lock (_calls)
				{
					return _calls.ToArray();
				}
			}
		}

		/// <summary>Fails the next call to <paramref name="identifier"/>, which is then not recorded.</summary>
		public void FailNext(string identifier)
		{
			lock (_calls)
			{
				_failingIdentifiers.Add(identifier);
			}
		}

		public int Count(string identifier)
		{
			return Calls.Count(x => x.Identifier == identifier);
		}

		public string LastArguments(string identifier)
		{
			return Calls.Last(x => x.Identifier == identifier).ArgumentsJson;
		}

		protected override void BeginInvokeJS(long taskId, string identifier, string? argsJson, JSCallResultType resultType, long targetInstanceId)
		{
			bool isFailing;
			lock (_calls)
			{
				isFailing = _failingIdentifiers.Remove(identifier);
				if (isFailing)
				{
					FailedCount++;
				}
				else
				{
					_calls.Add((identifier, argsJson ?? "[]"));
				}
			}

			var outcome = isFailing
				? $"false,\"{identifier} failed\""
				: resultType == JSCallResultType.JSObjectReference
					? $"true,{{\"__jsObjectId\":{Interlocked.Increment(ref _nextObjectId)}}}"
					: "true,null";
			DotNetDispatcher.EndInvokeJS(this, $"[{taskId},{outcome}]");
		}

		protected override void EndInvokeDotNet(DotNetInvocationInfo invocationInfo, in DotNetInvocationResult invocationResult)
		{
		}
	}

	/// <summary>
	/// The item set the interop module ends up holding, rebuilt from the calls it was sent by the same
	/// decoding <c>adaptive-virtualize-interop.js</c> does: <c>create</c> seeds a table of (key, size)
	/// slots, slot <c>i</c> being item <c>i</c>; <c>setItems(order, newKeys, newSizes, isTableReset)</c>
	/// appends the new slots, then reads the set from <c>order</c>, where a negative entry <c>-n</c>
	/// continues the previous slot with the next <c>n</c>, and <c>null</c> keeps the set as it is.
	/// </summary>
	private sealed class JsItemSet
	{
		private readonly List<string> _slotKeys = [];
		private readonly List<double> _slotSizes = [];

		public List<string> Keys { get; private set; } = [];
		public List<double> Sizes { get; private set; } = [];
		public int LargestTableSize { get; private set; }

		public static JsItemSet Replay(InteropRecorder recorder)
		{
			var jsItemSet = new JsItemSet();
			foreach (var (identifier, argumentsJson) in recorder.Calls)
			{
				using var arguments = JsonDocument.Parse(argumentsJson);
				var root = arguments.RootElement;
				switch (identifier)
				{
					case "create":
						jsItemSet.AddSlots(root[2], root[3]);
						jsItemSet.Keys = [.. jsItemSet._slotKeys];
						jsItemSet.Sizes = [.. jsItemSet._slotSizes];
						break;
					case "setItems":
						jsItemSet.ApplySetItems(root);
						break;
				}
			}

			return jsItemSet;
		}

		private void ApplySetItems(JsonElement arguments)
		{
			if (arguments[3].GetBoolean())
			{
				_slotKeys.Clear();
				_slotSizes.Clear();
			}

			AddSlots(arguments[1], arguments[2]);
			if (arguments[0].ValueKind == JsonValueKind.Null)
			{
				return;
			}

			Keys = [];
			Sizes = [];
			var previousSlot = -1;
			foreach (var entry in arguments[0].EnumerateArray().Select(x => x.GetInt32()))
			{
				var firstSlot = entry < 0
					? previousSlot + 1
					: entry;
				var slotCount = entry < 0
					? -entry
					: 1;
				for (var slot = firstSlot; slot < firstSlot + slotCount; slot++)
				{
					Keys.Add(_slotKeys[slot]);
					Sizes.Add(_slotSizes[slot]);
					previousSlot = slot;
				}
			}
		}

		private void AddSlots(JsonElement keys, JsonElement sizes)
		{
			_slotKeys.AddRange(keys.EnumerateArray().Select(x => x.GetString()!));
			_slotSizes.AddRange(sizes.EnumerateArray().Select(x => x.GetDouble()));
			LargestTableSize = Math.Max(LargestTableSize, _slotKeys.Count);
		}
	}
}
