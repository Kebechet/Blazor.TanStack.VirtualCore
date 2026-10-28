[!["Buy Me A Coffee"](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://www.buymeacoffee.com/kebechet)

# Blazor.TanStack.VirtualCore
[![NuGet Version](https://img.shields.io/nuget/v/Kebechet.Blazor.TanStack.VirtualCore)](https://www.nuget.org/packages/Kebechet.Blazor.TanStack.VirtualCore/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Kebechet.Blazor.TanStack.VirtualCore)](https://www.nuget.org/packages/Kebechet.Blazor.TanStack.VirtualCore/)
[![Build](https://github.com/Kebechet/Blazor.TanStack.VirtualCore/actions/workflows/build.yml/badge.svg)](https://github.com/Kebechet/Blazor.TanStack.VirtualCore/actions/workflows/build.yml)
[![codecov](https://codecov.io/gh/Kebechet/Blazor.TanStack.VirtualCore/graph/badge.svg)](https://codecov.io/gh/Kebechet/Blazor.TanStack.VirtualCore)
[![Storybook](https://img.shields.io/badge/storybook-live%20demo-ff4785)](https://kebechet.github.io/Blazor.TanStack.VirtualCore/)
![Last updated](https://img.shields.io/github/last-commit/Kebechet/Blazor.TanStack.VirtualCore/main?label=last%20updated)
[![Twitter](https://img.shields.io/twitter/url/https/twitter.com/samuel_sidor.svg?style=social&label=Follow%20samuel_sidor)](https://x.com/samuel_sidor)

Virtualization for Blazor lists whose items are **genuinely different sizes**, powered by [@tanstack/virtual-core](https://github.com/TanStack/virtual).

Every item is measured individually. Blazor keeps ownership of rendering - the JS side only answers "which indices are visible, and at what offsets", and the component renders that slice. Vertical, horizontal and multi-lane (masonry) layouts are all supported.

The virtual-core build is vendored and pinned inside the package, so there is no npm step, no CDN, and no `<script>` tag.

**[Live storybook](https://kebechet.github.io/Blazor.TanStack.VirtualCore/)** - interactive stories for every feature.

## Why not the built-in `Virtualize`?

The framework's `Virtualize` assumes one fixed `ItemSize`. .NET 11 adds variable-height support ([dotnet/aspnetcore#65158](https://github.com/dotnet/aspnetcore/issues/65158)), and for most lists **that will be the right choice** - it is in the box and needs no JS.

Reach for this package when the approximation is the problem:

| | Built-in `Virtualize` (.NET 11) | This package |
|---|---|---|
| Row sizing | weighted running average, no per-item storage | exact measurement, cached per item key |
| Cache across item-set changes | re-measured | preserved per key, so a filter change keeps known heights |
| Content resize at a stable key | manual `RefreshDataAsync()` | targeted `InvalidateItemSize(key)` |

If your rows differ by a little, take the built-in one. If a 60px row sits next to a 400px expanded one, an average places both wrong and the list visibly snaps as measurements arrive.

## Installation

```bash
dotnet add package Kebechet.Blazor.TanStack.VirtualCore
```

No service registration is needed - the component imports its own interop module.

## Usage

The component fills its nearest scrollable ancestor, which it finds by walking up for a computed `overflow-y` of `auto` or `scroll`, falling back to the document scroller.

```razor
@using Kebechet.Blazor.TanStack.VirtualCore

<div style="height: 100vh; overflow-y: auto;">
    <AdaptiveVirtualize TItem="Exercise"
                        Items="_exercises"
                        ItemKeyExtractor="@(x => x.Id.ToString())"
                        ItemSizeEstimator="@(x => x.IsExpanded ? 240 : 72)"
                        Options="_options">
        <ItemContent>
            <ExerciseRow Exercise="context" />
        </ItemContent>
    </AdaptiveVirtualize>
</div>

@code {
    private readonly VirtualizeOptions _options = new() { Overscan = 5 };
}
```

`ItemKeyExtractor` is load-bearing. It is both Blazor's `@key` and the measurement-cache key, so it must be stable for an item across renders and unique between items - otherwise measurements attach to the wrong item.

`Options` carries the whole upstream surface and is reactive: assigning a **new** instance pushes the changed values to the live virtualizer. Mutating the existing instance in place does nothing, because the component compares references.

### Layouts

```csharp
// A horizontal card rail
new VirtualizeOptions { IsHorizontal = true, Gap = 8 }

// A three-column masonry grid, balanced by measured size
new VirtualizeOptions { Lanes = 3, LaneAssignmentMode = LaneAssignmentMode.Measured, Gap = 10 }

// A log tail that follows new entries
new VirtualizeOptions { FollowOnAppend = FollowOnAppend.Instant, AnchorTo = ScrollAnchor.End }

// Virtualize against the document rather than a scrollable ancestor
new VirtualizeOptions { UseWindowScroller = true }
```

In a multi-lane layout each placement reports the `Lane` it landed in, and the component turns that into the item's cross-axis position and size - so your `ItemContent` only owns its own inner spacing. An outer margin would fight the computed lane width.

### Keeping items rendered off-screen

```csharp
new VirtualizeOptions { AlwaysRenderedIndexes = [0, 1] }
```

Those indices stay in the DOM however far away you scroll - a sticky header, a pinned selection, a row being dragged. This is upstream's `rangeExtractor`, reshaped: a JS predicate would cost an interop round-trip on every range computation, so the wrapper takes the indices and compiles the extractor itself.

### Invalidating one row

When a row's content changes shape but its key does not - a group toggling collapsed to expanded - the preserved measurement is stale:

```csharp
private AdaptiveVirtualize<Exercise>? _virtualize;

private async Task ToggleGroup(Exercise exercise)
{
    exercise.IsExpanded = !exercise.IsExpanded;
    await _virtualize!.InvalidateItemSize(exercise.Id.ToString());
}
```

### Scrolling and inspecting

```csharp
await _virtualize.ScrollToIndex(42, ScrollAlignment.Center, ScrollBehavior.Smooth);
await _virtualize.ScrollToOffset(2000);
await _virtualize.ScrollToEnd(ScrollBehavior.Instant);
await _virtualize.ScrollBy(300);
await _virtualize.ScrollToTop();

// The user started scrolling themselves: stop the scroll above from re-aiming at its target
await _virtualize.CancelScroll();

// Override a size directly, without waiting for the DOM to be measured
await _virtualize.ResizeItem(index: 0, size: 200);

// Live state: offset, total size, isScrolling, isAtEnd, direction, rendered range
var snapshot = await _virtualize.GetSnapshot();

// Save measurements, then restore them on a later mount to skip re-measuring
var measurements = await _virtualize.TakeSnapshot();
```

These are safe to fire off without awaiting while the component may be going away. Called after disposal they do nothing, and one still in flight when the component is disposed completes without throwing: a query returns its default, and the failure is logged - at Debug when the JS side simply went away.

### Two-phase rendering

### Directional overscan

`Overscan` buffers both sides equally. `OverscanLeading` / `OverscanTrailing` spend more of that budget on the side the user is scrolling towards, which is where the next items are actually needed:

```csharp
private readonly VirtualizeOptions _options = new()
{
    Overscan = 5,          // the fallback for either side
    OverscanLeading = 10,  // ahead of travel
    OverscanTrailing = 5,  // behind travel - the floor a reversal starts from
};
```

The leading side follows the virtualizer's scroll direction and flips when the user reverses; a list that has never scrolled is treated as scrolling forward. Biasing toward travel is the default in react-window's [`_getRangeToRender`](https://github.com/bvaughn/react-window/blob/1.8.11/src/createListComponent.js), react-virtualized's [`defaultOverscanIndicesGetter`](https://github.com/bvaughn/react-virtualized/blob/master/source/Grid/defaultOverscanIndicesGetter.js) and Angular CDK's [`FixedSizeVirtualScrollStrategy`](https://github.com/angular/components/blob/main/src/cdk/scrolling/fixed-size-virtual-scroll.ts).

⚠️ **`OverscanTrailing` is held at all times, not only while scrolling** - which is where this deliberately parts company with react-window. React re-renders when its `isScrolling` state flips, so its range genuinely recomputes at rest. virtual-core memoizes `getVirtualIndexes` on `[rangeExtractor, overscan, count, startIndex, endIndex, lanes]`, and `isScrolling` is not among them, so an extractor that read it would keep returning the scrolling-time answer until the visible range moved again - the bias would outlive the scroll rather than lapse with it.

Holding the trailing side instead means a reversal is never worse than a symmetric window of the same size. Leave `OverscanTrailing` at whatever `Overscan` you would otherwise have used and treat `OverscanLeading` as the addition. Raise it further when unmounting an item is expensive - a row owning a `<video>`, say - since those items are already mounted and holding them costs far less than rebuilding them.

`OnLayoutMeasured` fires after each measurement pass. Use it when extra content must be appended only once the preceding row's real height is known, so the new rows land correctly on first paint instead of estimating and snapping.

It is a plain `Func<Task>` rather than an `EventCallback` on purpose: `EventCallback` calls `StateHasChanged` on the receiver after the handler returns, which on a page with a debounced search input re-renders the bound input with a stale value and clobbers in-flight keystrokes.

## Coverage vs. @tanstack/virtual-core 3.19.8

**Complete.** Every `VirtualizerOptions` member is reachable, and every `Virtualizer` member that means something to a .NET caller is surfaced.

| Axis | virtual-core | This package |
|---|---:|---:|
| `VirtualizerOptions` members | 36 | 36 |

**Set through `Options`** (25): `overscan`, `horizontal`, `lanes`, `laneAssignmentMode`, `gap`, `paddingStart`, `paddingEnd`, `scrollPaddingStart`, `scrollPaddingEnd`, `scrollMargin`, `initialOffset`, `initialRect`, `initialMeasurementsCache`, `anchorTo`, `followOnAppend`, `scrollEndThreshold`, `isScrollingResetDelay`, `useScrollendEvent`, `isRtl`, `enabled`, `useAnimationFrameWithResizeObserver`, `useCachedMeasurements`, `indexAttribute`, `debug`, plus `rangeExtractor` reshaped as `AlwaysRenderedIndexes` and `OverscanLeading` / `OverscanTrailing`.

**Supplied by the component** (11), because virtual-core requires them as functions called on every scroll frame - marshalling them to .NET would mean an interop round-trip per frame: `count` (from `Items`), `estimateSize` (`ItemSizeEstimator` / `EstimatedItemSize`), `getItemKey` (`ItemKeyExtractor`), `measureElement`, `onChange`, and the scroll-source trio `getScrollElement` / `observeElementOffset` / `observeElementRect` / `scrollToFn` - which `UseWindowScroller` switches between the element and window variants (`observeWindowOffset`, `observeWindowRect`, `windowScroll`).

**Methods:** `ScrollToIndex`, `ScrollToOffset`, `ScrollToEnd`, `ScrollBy`, `ScrollToTop`, `CancelScroll`, `ResizeItem`, `Measure`, `GetTotalSize`, `GetVirtualIndexes`, `GetOffsetForIndex`, `GetVirtualItemForOffset`, `GetDistanceFromEnd`, `GetSnapshot` (scroll offset, total size, `isScrolling`, `isAtEnd`, direction and the rendered range), `TakeSnapshot`, plus `InvalidateItemSize` - which has no upstream equivalent; it evicts one key from the measurement cache and bumps the cache version.

**Deliberately not surfaced:** virtual-core's internal plumbing - `elementsCache`, `targetWindow`, `updateDeps`, `calculateRange`, `indexFromElement`, `shouldAdjustScrollPositionOnItemSizeChange`, `scrollAdjustments`, `setOptions` (driven by the `Options` parameter) and the raw `options` / `measurementsCache` / `itemSizeCache` fields, which `GetSnapshot` and `TakeSnapshot` expose in a typed form instead.

⚠️ **`isAtEnd` is a method upstream, not a property** (`(threshold?: number) => boolean`). Reading it as a property yields the function object, which is never `=== true` - so a snapshot would report "not at end" forever. Pinned by `ScrollToEnd_ReachesTheEndOfTheList`.

Coverage is measured against virtual-core's published `dist/esm/index.d.ts` - the `VirtualizerOptions` interface and the `Virtualizer` class - excluding underscore-prefixed internals.

## A note on upstream internals

The interop calls three underscore-prefixed virtual-core members: `_willUpdate()`, `_didMount()`, and direct reads and writes of `itemSizeCache` (declared public in the `.d.ts`, but paired with a `private itemSizeCacheVersion`). They are what make measurement-cache preservation across an item-set change possible, and virtual-core exposes no public equivalent.

This is a real coupling: a virtual-core release could change them without it being a documented breaking change. It is why the package pins an exact version rather than floating, and why every version bump re-verifies those three members before shipping.

## Stopgaps for upstream fixes

Behaviour that belongs in virtual-core, carried here only until the upstream fix ships:

| Behaviour | Upstream | Stopgap |
|---|---|---|
| A `ScrollToIndex`, `ScrollToOffset`, `ScrollToEnd` or `ScrollBy` issued by code while a touch fling is still moving the list overshoots its target on Android: Chrome carries the fling on from the written position. virtual-core stops the fling first only on iOS 27. | https://github.com/TanStack/virtual/pull/1347 (issue https://github.com/TanStack/virtual/issues/1346) | `runScrollCommand` in `wwwroot/adaptive-virtualize-interop.js` hides the scroll axis's overflow for two frames before the write. Pinned by `ScrollToIndex_WhileATouchFlingIsMovingTheList_StopsTheMomentumBeforeWriting`. |

⚠️ **Remove the stopgap together with the bump to the virtual-core release that contains the fix.** Until then it also runs on iOS, where it is not needed and costs its two frames, because telling the platforms apart would mean copying virtual-core's private user-agent checks.

## Vendored library provenance

| | |
|---|---|
| Version | @tanstack/virtual-core 3.19.8 |
| File | `wwwroot/tanstack-virtual-core.js` |
| Source | jsDelivr rollup bundle of npm `@tanstack/virtual-core@3.19.8` (`+esm`) |
| SHA-256 | `52c91d57d1bf1bd22bea2c3a64cd4cfcceea1808b747cbfd3bbbc13ea0ca487a` |

⚠️ **Deliberately the bundled build, not the npm tarball's `dist/esm/index.js`.**

That file is not self-contained - it imports `./lazy-measurements.js` and `./utils.js` as siblings. Vendoring it alone gives a module that fetches fine and then fails to evaluate, and Blazor's fingerprinted import map turns the missing sibling into a confusing `Failed to fetch dynamically imported module` naming the *outer* module. The jsDelivr bundle is a single self-contained ESM file built from that same npm release, with the source stated in its own header. Its private-API surface (`_willUpdate`, `_didMount`, `itemSizeCache`, `itemSizeCacheVersion`) is re-verified on every bump.

## License

[MIT](LICENSE). @tanstack/virtual-core is itself [MIT licensed](https://github.com/TanStack/virtual/blob/main/LICENSE).
