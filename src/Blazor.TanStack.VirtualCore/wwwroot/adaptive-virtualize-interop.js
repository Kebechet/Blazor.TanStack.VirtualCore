'use strict';

// Relative, so the module resolves correctly under any <base href>. An absolute /_content/... path
// breaks the moment the app is hosted from a sub-path.
import {
	Virtualizer,
	observeElementOffset,
	observeElementRect,
	observeWindowOffset,
	observeWindowRect,
	elementScroll,
	windowScroll,
	measureElement,
	defaultRangeExtractor,
} from './tanstack-virtual-core.js';

export function create(rootElement, dotNetRef, initialDescriptors, estimatedItemSize, options) {
	// Blazor queues this call, so the element can already be gone by the time it runs - disposing
	// the component, swapping a route, or switching a story marshals the ElementReference as null.
	// Returning null instead of constructing leaves the caller's _jsInstance null, which every
	// method already guards against; throwing here would surface as an unhandled render exception
	// for a component that no longer exists.
	if (!rootElement || !rootElement.isConnected) {
		return null;
	}

	return new AdaptiveVirtualizeInstance(rootElement, dotNetRef, initialDescriptors, estimatedItemSize, options);
}

/**
 * Blazor's interop serializer emits every unset nullable as JSON null. Passing those straight
 * through would override virtual-core's own defaults with null, so unset members are dropped rather
 * than forwarded. The members that need reshaping are handled by the caller, not here.
 */
function compact(source) {
	const result = {};
	if (!source) {
		return result;
	}

	for (const [key, value] of Object.entries(source)) {
		if (value !== null && value !== undefined) {
			result[key] = value;
		}
	}

	return result;
}

class AdaptiveVirtualizeInstance {
	constructor(rootElement, dotNetRef, initialDescriptors, estimatedItemSize, options) {
		this.rootElement = rootElement;
		this.dotNetRef = dotNetRef;
		this.descriptors = initialDescriptors;
		this.estimatedItemSize = estimatedItemSize;
		this.options = compact(options);
		this.scrollContainer = this.findScrollContainer(rootElement);
		this.lastVirtualItems = null;
		this.lastTotalSize = -1;
		this.suppressNotifications = false;
		// Keys already measured during the current item-set lifetime. measureElement forces a
		// layout (getBoundingClientRect), which on iOS WKWebView is 10-50x more expensive than on
		// Chrome. Skipping already-measured keys drops a scroll-time measure pass from 100-1000 ms
		// to a few ms: only freshly virtualized items pay the cost.
		this.measuredKeys = new Set();

		this.virtualizer = new Virtualizer(this.buildVirtualizerOptions());

		this.virtualizer._willUpdate();
		this.cleanupMount = this.virtualizer._didMount();
		this.notifyChange();
	}

	get isWindowScroller() {
		return this.options.useWindowScroller === true;
	}

	/**
	 * Maps the wrapper's option object onto virtual-core's. The members virtual-core needs as
	 * functions are supplied here rather than marshalled from .NET, because they are called on
	 * every scroll frame and an interop round-trip per frame would dominate the cost.
	 */
	buildVirtualizerOptions() {
		const {
			useWindowScroller,
			alwaysRenderedIndexes,
			followOnAppend,
			laneAssignmentMode,
			anchorTo,
			...passThrough
		} = this.options;

		const resolved = {
			...passThrough,
			count: this.descriptors.length,
			estimateSize: (index) => this.descriptors[index]?.estimatedSize ?? this.estimatedItemSize,
			getItemKey: (index) => this.descriptors[index]?.key ?? String(index),
			measureElement,
			onChange: () => this.notifyChange(),
		};

		if (this.isWindowScroller) {
			resolved.getScrollElement = () => globalThis;
			resolved.observeElementOffset = observeWindowOffset;
			resolved.observeElementRect = observeWindowRect;
			resolved.scrollToFn = windowScroll;
		} else {
			resolved.getScrollElement = () => this.scrollContainer;
			resolved.observeElementOffset = observeElementOffset;
			resolved.observeElementRect = observeElementRect;
			resolved.scrollToFn = elementScroll;
		}

		// Enums cross interop as their C# names; virtual-core wants its own lowercase literals.
		if (laneAssignmentMode) {
			resolved.laneAssignmentMode = laneAssignmentMode.toLowerCase();
		}
		if (anchorTo) {
			resolved.anchorTo = anchorTo.toLowerCase();
		}
		// Upstream types this as `boolean | ScrollBehavior`, which the wrapper flattens into one
		// enum. 'None' is the false arm; everything else is the behaviour string.
		if (followOnAppend) {
			resolved.followOnAppend = followOnAppend === 'None' ? false : followOnAppend.toLowerCase();
		}

		// rangeExtractor is a JS function called on every range computation, so it cannot be
		// marshalled to .NET without an interop round-trip per scroll frame. The wrapper models it
		// as a list of indices to always keep rendered, compiled here into a real extractor that
		// unions them with virtual-core's own default range.
		if (Array.isArray(alwaysRenderedIndexes) && alwaysRenderedIndexes.length > 0) {
			resolved.rangeExtractor = (range) => {
				const indexes = new Set([...alwaysRenderedIndexes, ...defaultRangeExtractor(range)]);
				return [...indexes].filter(i => i >= 0 && i < range.count).sort((a, b) => a - b);
			};
		}

		return resolved;
	}

	findScrollContainer(element) {
		// In window-scroller mode the scroll source is the window, so walking the ancestor chain
		// would pick a container that is then never used.
		if (this.isWindowScroller) {
			return document.scrollingElement || document.documentElement;
		}

		let current = element?.parentElement ?? null;
		while (current) {
			const overflowY = window.getComputedStyle(current).overflowY;
			const overflowX = window.getComputedStyle(current).overflowX;
			const scrolls = (overflow) => overflow === 'auto' || overflow === 'scroll';
			if (scrolls(overflowY) || scrolls(overflowX)) {
				return current;
			}
			current = current.parentElement;
		}
		return document.scrollingElement || document.documentElement;
	}

	setOptions(newOptions) {
		this.options = compact(newOptions);
		this.virtualizer.setOptions(this.buildVirtualizerOptions());
		// _willUpdate is the ONLY place virtual-core resolves getScrollElement() and attaches its
		// rect and offset observers, and it early-returns unless the element identity changed.
		// Upstream's own framework adapters call it on every render; calling it once at construction
		// is not enough. Without this, `enabled` flipping back to true never re-attaches the
		// observers and the list stays frozen at zero items - silently, with no error.
		this.virtualizer._willUpdate();
		this.virtualizer.measure();
		this.measuredKeys.clear();
		this.notifyChange();
	}

	notifyChange() {
		if (this.suppressNotifications) {
			return;
		}

		const virtualItems = this.virtualizer.getVirtualItems();
		const totalSize = this.virtualizer.getTotalSize();

		if (totalSize === this.lastTotalSize && this.areVirtualItemsEqual(virtualItems, this.lastVirtualItems)) {
			return;
		}

		this.lastVirtualItems = virtualItems;
		this.lastTotalSize = totalSize;

		this.dotNetRef.invokeMethodAsync('OnVirtualItemsChanged', virtualItems.map(toPayload), totalSize);
	}

	areVirtualItemsEqual(a, b) {
		if (a === b) return true;
		if (!a || !b) return false;
		if (a.length !== b.length) return false;
		for (let i = 0; i < a.length; i++) {
			// The key must be compared too. When two item sets produce the same geometry but a
			// different item at the same index, index/start/size all match while the item's identity
			// changed. Without the key check notifyChange bails, .NET keeps stale placements, and
			// the render loop skips the slot on the key mismatch until some later scroll happens to
			// produce different geometry. Lane matters for the same reason under multi-lane layouts.
			if (a[i].index !== b[i].index ||
				a[i].start !== b[i].start ||
				a[i].size !== b[i].size ||
				a[i].lane !== b[i].lane ||
				a[i].key !== b[i].key) {
				return false;
			}
		}
		return true;
	}

	measureRenderedRows() {
		const attribute = this.options.indexAttribute ?? 'data-index';
		const elements = this.rootElement.querySelectorAll(`[${attribute}]`);
		if (elements.length === 0) {
			return;
		}

		// Suppress per-element onChange so we don't fire one .NET round-trip per measured item. The
		// virtualizer calls onChange synchronously inside measureElement whenever a size differs
		// from cache, which is 5-10 callbacks per render pass - each one a separate Blazor render.
		// A single notify is flushed at the end instead.
		this.suppressNotifications = true;
		try {
			elements.forEach(el => {
				const indexAttr = el.getAttribute(attribute);
				const index = indexAttr !== null ? parseInt(indexAttr, 10) : -1;
				const key = index >= 0 ? this.descriptors[index]?.key : undefined;
				if (key && this.measuredKeys.has(key)) {
					return;
				}
				this.virtualizer.measureElement(el);
				if (key) {
					this.measuredKeys.add(key);
				}
			});
		} finally {
			this.suppressNotifications = false;
		}

		this.notifyChange();
	}

	setItems(newDescriptors) {
		// Snapshot existing per-key measurements before measure() wipes them. An item-set change
		// (a filter swap, an inserted child row) must not cost the measurements of items whose
		// content did not change - otherwise expanding a group clears the group's measured size,
		// falls back to the estimate, and the inserted items land on top of the group's bottom edge
		// until the next measure pass.
		const sizeSnapshot = this.virtualizer.itemSizeCache instanceof Map
			? new Map(this.virtualizer.itemSizeCache)
			: new Map();

		this.descriptors = newDescriptors;

		// Drop measured-key tracking for keys no longer present. Their itemSizeCache entries are
		// simply not restored below, which evicts them.
		const newKeys = new Set(newDescriptors.map(d => d.key));
		for (const key of [...this.measuredKeys]) {
			if (!newKeys.has(key)) {
				this.measuredKeys.delete(key);
			}
		}

		this.virtualizer.setOptions({
			...this.virtualizer.options,
			count: newDescriptors.length,
		});

		// measure() does two things: it reassigns itemSizeCache to an empty Map, and it
		// SYNCHRONOUSLY notifies. The notify is the trap - at that moment the cache is empty, so
		// getVirtualItems falls back to estimates and ships estimate-based positions to .NET,
		// before the restore below has run. Suppress that notify, restore, then notify once, so
		// .NET only ever sees placements computed from the preserved measurements.
		this.suppressNotifications = true;
		try {
			this.virtualizer.measure();
		} finally {
			this.suppressNotifications = false;
		}

		if (this.virtualizer.itemSizeCache instanceof Map) {
			for (const [key, size] of sizeSnapshot) {
				if (newKeys.has(key) && this.measuredKeys.has(key)) {
					this.virtualizer.itemSizeCache.set(key, size);
				}
			}
			// The virtualizer memoizes measurements on [measurementOptions, itemSizeCacheVersion].
			// Writing straight into the Map leaves that version untouched, so the memo can serve a
			// result computed from the empty cache measure() just installed. measure() happens to
			// bump the version first today, which makes the restore work by ordering rather than by
			// contract - bump it explicitly so a reordering upstream cannot silently break this.
			this.virtualizer.itemSizeCacheVersion++;
		}

		// _willUpdate is also the CONSUMER of pendingScrollAnchor, which setOptions fills when
		// anchorTo / followOnAppend react to the count and edge-key change above - the follow is
		// only scheduled there, and _willUpdate is where scrollToEnd actually runs. Upstream's
		// framework adapters call _willUpdate on every render; without this call a chat-style list
		// schedules the follow and silently drops it, staying put while items append below.
		// Called after the cache restore so the scroll target is computed from real measurements.
		this.virtualizer._willUpdate();

		this.notifyChange();
	}

	/**
	 * Drops the cached measurement for one key. Used when an item's content changed shape while its
	 * key stayed the same (a group toggling collapsed/expanded changes its size by 100+px without
	 * changing identity). Without this, setItems would preserve the now-stale measurement.
	 */
	invalidateItemSize(key) {
		this.measuredKeys.delete(key);
		if (this.virtualizer.itemSizeCache instanceof Map) {
			this.virtualizer.itemSizeCache.delete(key);
			this.virtualizer.itemSizeCacheVersion++;
		}
	}

	scrollToIndex(index, align, behavior) {
		this.virtualizer.scrollToIndex(index, { align, behavior });
	}

	scrollToOffset(offset, align, behavior) {
		this.virtualizer.scrollToOffset(offset, { align, behavior });
	}

	scrollToEnd(behavior) {
		this.virtualizer.scrollToEnd({ behavior });
	}

	scrollBy(delta, behavior) {
		this.virtualizer.scrollBy(delta, { behavior });
	}

	resizeItem(index, size) {
		this.virtualizer.resizeItem(index, size);
	}

	measure() {
		this.measuredKeys.clear();
		this.virtualizer.measure();
		this.notifyChange();
	}

	getTotalSize() {
		return this.virtualizer.getTotalSize();
	}

	getVirtualIndexes() {
		return this.virtualizer.getVirtualIndexes();
	}

	getOffsetForIndex(index, align) {
		// Upstream returns [offset, alignment]; only the offset is meaningful to a caller.
		const result = this.virtualizer.getOffsetForIndex(index, align);
		return Array.isArray(result) ? result[0] : result;
	}

	getVirtualItemForOffset(offset) {
		const item = this.virtualizer.getVirtualItemForOffset(offset);
		return item ? toPayload(item) : null;
	}

	getDistanceFromEnd() {
		return this.virtualizer.getDistanceFromEnd();
	}

	getSnapshot() {
		const range = this.virtualizer.range;
		return {
			scrollOffset: this.virtualizer.scrollOffset ?? 0,
			totalSize: this.virtualizer.getTotalSize(),
			// isScrolling and scrollDirection are plain properties, but isAtEnd is a METHOD taking
			// an optional threshold - reading it as a property yields the function object, which is
			// silently never === true, so the snapshot would report "not at end" forever.
			isScrolling: this.virtualizer.isScrolling === true,
			isAtEnd: typeof this.virtualizer.isAtEnd === 'function'
				? this.virtualizer.isAtEnd() === true
				: this.virtualizer.isAtEnd === true,
			scrollDirection: this.virtualizer.scrollDirection ?? null,
			startIndex: range ? range.startIndex : -1,
			endIndex: range ? range.endIndex : -1,
		};
	}

	takeSnapshot() {
		// measurementsCache is the array shape initialMeasurementsCache expects back, so a snapshot
		// taken here can be handed straight to a later mount.
		const measurements = this.virtualizer.measurementsCache ?? [];
		return measurements.map(toPayload);
	}

	dispose() {
		if (this.cleanupMount) {
			try { this.cleanupMount(); } catch { /* already torn down */ }
			this.cleanupMount = null;
		}
	}
}

/** virtual-core keys can be number | string | bigint; .NET always reads them as string. */
function toPayload(virtualItem) {
	return {
		index: virtualItem.index,
		start: virtualItem.start,
		size: virtualItem.size,
		end: virtualItem.end,
		lane: virtualItem.lane ?? 0,
		key: String(virtualItem.key),
	};
}
