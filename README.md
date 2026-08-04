[!["Buy Me A Coffee"](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://www.buymeacoffee.com/kebechet)

# Blazor.TanStackVirtual
[![NuGet Version](https://img.shields.io/nuget/v/Kebechet.Blazor.TanStackVirtual)](https://www.nuget.org/packages/Kebechet.Blazor.TanStackVirtual/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Kebechet.Blazor.TanStackVirtual)](https://www.nuget.org/packages/Kebechet.Blazor.TanStackVirtual/)
[![Build](https://github.com/Kebechet/Blazor.TanStackVirtual/actions/workflows/build.yml/badge.svg)](https://github.com/Kebechet/Blazor.TanStackVirtual/actions/workflows/build.yml)
[![codecov](https://codecov.io/gh/Kebechet/Blazor.TanStackVirtual/graph/badge.svg)](https://codecov.io/gh/Kebechet/Blazor.TanStackVirtual)
![Last updated](https://img.shields.io/github/last-commit/Kebechet/Blazor.TanStackVirtual/main?label=last%20updated)
[![Twitter](https://img.shields.io/twitter/url/https/twitter.com/samuel_sidor.svg?style=social&label=Follow%20samuel_sidor)](https://x.com/samuel_sidor)

Virtualization for Blazor lists whose rows are **genuinely different heights**, powered by [@tanstack/virtual-core](https://github.com/TanStack/virtual).

Every row is measured individually. Blazor keeps ownership of rendering - the JS side only answers "which indices are visible, and at what offsets", and the component renders that slice.

The virtual-core build is vendored and pinned inside the package, so there is no npm step, no CDN, and no `<script>` tag.

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
dotnet add package Kebechet.Blazor.TanStackVirtual
```

No service registration is needed - the component imports its own interop module.

## Usage

The component fills its nearest scrollable ancestor, which it finds by walking up for a computed `overflow-y` of `auto` or `scroll`, falling back to the document scroller.

```razor
@using Kebechet.Blazor.TanStackVirtual

<div style="height: 100vh; overflow-y: auto;">
    <AdaptiveVirtualize TItem="Exercise"
                        Items="_exercises"
                        ItemKeyExtractor="@(x => x.Id.ToString())"
                        ItemSizeEstimator="@(x => x.IsExpanded ? 240 : 72)"
                        Overscan="5">
        <ItemContent>
            <ExerciseRow Exercise="context" />
        </ItemContent>
    </AdaptiveVirtualize>
</div>
```

`ItemKeyExtractor` is load-bearing. It is both Blazor's `@key` and the measurement-cache key, so it must be stable for a row across renders and unique between rows - otherwise measurements attach to the wrong row.

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

### Scrolling

```csharp
await _virtualize.ScrollToIndex(42, ScrollAlignment.Center, ScrollBehavior.Smooth);
await _virtualize.ScrollToTop();
```

### Two-phase rendering

`OnLayoutMeasured` fires after each measurement pass. Use it when extra content must be appended only once the preceding row's real height is known, so the new rows land correctly on first paint instead of estimating and snapping.

It is a plain `Func<Task>` rather than an `EventCallback` on purpose: `EventCallback` calls `StateHasChanged` on the receiver after the handler returns, which on a page with a debounced search input re-renders the bound input with a stale value and clobbers in-flight keystrokes.

## Coverage vs. @tanstack/virtual-core 3.17.7

This wrapper is a deliberately narrow, opinionated surface over virtual-core - a vertical, single-lane list - not a general binding.

| Axis | virtual-core | This package |
|---|---:|---:|
| `VirtualizerOptions` members | 36 | 4 exposed, 6 wired internally |
| Public `Virtualizer` members | 35 | 3 surfaced |

**Exposed as parameters:** `count` (from `Items`), `estimateSize` (`ItemSizeEstimator` / `EstimatedItemSize`), `getItemKey` (`ItemKeyExtractor`), `overscan` (`Overscan`).

**Wired internally with fixed values:** `getScrollElement` (auto-detected ancestor), `observeElementOffset`, `observeElementRect`, `scrollToFn`, `measureElement`, `onChange`.

**Surfaced methods:** `scrollToIndex` (as `ScrollToIndex` / `ScrollToTop`), plus `InvalidateItemSize`, which has no direct upstream equivalent - it evicts one key from the measurement cache and bumps the cache version.

**Not covered:** `horizontal`, `lanes` (masonry / multi-column), `gap`, `paddingStart` / `paddingEnd`, `scrollMargin`, `scrollPadding*`, `rangeExtractor`, `initialMeasurementsCache`, `isRtl`, `debug`, `enabled`, and the window-scroller mode (`observeWindowOffset` / `observeWindowRect` / `windowScroll`). Also unsurfaced: `scrollToOffset`, `scrollToEnd`, `scrollBy`, `resizeItem`, `takeSnapshot`.

There is no `AdditionalAttributes` escape hatch to reach the rest - the virtualizer is constructed inside the interop module. Open an issue for anything you need and it can be modelled explicitly; `horizontal` and `lanes` are the obvious next candidates.

Coverage is measured against virtual-core's published `dist/esm/index.d.ts` - the `VirtualizerOptions` interface and the `Virtualizer` class - excluding underscore-prefixed internals.

## A note on upstream internals

The interop calls three underscore-prefixed virtual-core members: `_willUpdate()`, `_didMount()`, and direct reads and writes of `itemSizeCache` (declared public in the `.d.ts`, but paired with a `private itemSizeCacheVersion`). They are what make measurement-cache preservation across an item-set change possible, and virtual-core exposes no public equivalent.

This is a real coupling: a virtual-core release could change them without it being a documented breaking change. It is why the package pins an exact version rather than floating, and why every version bump re-verifies those three members before shipping.

## Vendored library provenance

| | |
|---|---|
| Version | @tanstack/virtual-core 3.17.7 |
| File | `wwwroot/tanstack-virtual-core.esm.js` |
| Source | npm tarball `virtual-core-3.17.7.tgz`, `package/dist/esm/index.js` |
| SHA-256 | `255d29a9969a23e9867a11fb323ccac5a97c95bb765e1f3b68e9715dd12362bd` |

## License

[MIT](LICENSE). @tanstack/virtual-core is itself [MIT licensed](https://github.com/TanStack/virtual/blob/main/LICENSE).
