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

export function create(rootElement, dotNetRef, itemKeys, itemSizes, estimatedItemSize, options) {
	// Blazor queues this call, so the element can already be gone by the time it runs - disposing
	// the component, swapping a route, or switching a story marshals the ElementReference as null.
	// Returning null instead of constructing leaves the caller's _jsInstance null, which every
	// method already guards against; throwing here would surface as an unhandled render exception
	// for a component that no longer exists.
	if (!rootElement || !rootElement.isConnected) {
		return null;
	}

	return new AdaptiveVirtualizeInstance(rootElement, dotNetRef, itemKeys, itemSizes, estimatedItemSize, options);
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

const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));

class AdaptiveVirtualizeInstance {
	// Keys and sizes arrive as two parallel arrays rather than one array of objects: this covers the
	// WHOLE item set, not just the rendered window, and it crosses the interop boundary as JSON on
	// every item-set change. For a few thousand items the object form cost that many allocations on
	// the .NET side and a much larger payload.
	constructor(rootElement, dotNetRef, itemKeys, itemSizes, estimatedItemSize, options) {
		this.rootElement = rootElement;
		this.dotNetRef = dotNetRef;
		this.itemKeys = itemKeys ?? [];
		this.itemSizes = itemSizes ?? [];
		this.estimatedItemSize = estimatedItemSize;
		this.options = compact(options);
		this.scrollContainer = this.findScrollContainer(rootElement);
		this.lastVirtualItems = null;
		this.lastTotalSize = -1;
		this.suppressNotifications = false;
		this.pendingNotification = null;
		this.insideResizeObserver = false;
		// Keys already measured during the current item-set lifetime. measureElement forces a
		// layout (getBoundingClientRect), which on iOS WKWebView is 10-50x more expensive than on
		// Chrome. Skipping already-measured keys drops a scroll-time measure pass from 100-1000 ms
		// to a few ms: only freshly virtualized items pay the cost.
		this.measuredKeys = new Set();

		// What runScrollCommand needs to know about the user's input. Listened for on the window, so a
		// tap on a button outside the list counts too - that tap is how a jump gets issued mid-fling.
		this.touchActive = false;
		this.lastInputWasTouch = false;
		this.momentumStop = null;
		this.inputListeners = {
			pointerdown: event => { this.lastInputWasTouch = event.pointerType === 'touch'; },
			wheel: () => { this.lastInputWasTouch = false; },
			touchstart: () => { this.touchActive = true; },
			touchend: () => { this.touchActive = false; },
			touchcancel: () => { this.touchActive = false; },
		};
		for (const [type, listener] of Object.entries(this.inputListeners)) {
			window.addEventListener(type, listener, { capture: true, passive: true });
		}

		this.virtualizer = new Virtualizer(this.buildVirtualizerOptions());

		this.virtualizer._willUpdate();
		this.cleanupMount = this.virtualizer._didMount();

		// virtual-core re-issues a scroll write the browser clamped - the end anchor pushing past a
		// container that has not grown yet - from _willUpdate, which its framework adapters call
		// after every commit. Here the container is sized by a Blazor render one interop round-trip
		// after onChange, so the container resizing is that commit.
		this.containerObserver = typeof ResizeObserver === 'function'
			? new ResizeObserver(() => {
				this.markInsideResizeObserver();
				this.virtualizer._willUpdate();
			})
			: null;
		this.containerObserver?.observe(rootElement);

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
			overscanLeading,
			overscanTrailing,
			followOnAppend,
			laneAssignmentMode,
			anchorTo,
			...passThrough
		} = this.options;

		const resolved = {
			...passThrough,
			count: this.itemKeys.length,
			estimateSize: (index) => this.itemSizes[index] ?? this.estimatedItemSize,
			getItemKey: (index) => this.itemKeys[index] ?? String(index),
			measureElement: (element, entry, instance) => {
				if (entry) {
					this.markInsideResizeObserver();
				}
				return measureElement(element, entry, instance);
			},
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
			resolved.observeElementRect = (instance, callback) => observeElementRect(instance, rect => {
				this.markInsideResizeObserver();
				callback(rect);
			});
			resolved.scrollToFn = elementScroll;
		}

		// virtual-core negates scrollLeft when it reads the offset under isRtl, but writes the offset
		// back as is - and in a right-to-left container scrollLeft runs from 0 down to negative
		// values, so a positive write clamps to the start and every scroll command goes nowhere.
		// Negating it on the way out matches the read.
		if (passThrough.isRtl && passThrough.horizontal) {
			const write = resolved.scrollToFn;
			resolved.scrollToFn = (offset, { adjustments = 0, behavior }, instance) =>
				write(-(offset + adjustments), { behavior }, instance);
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
		// marshalled to .NET without an interop round-trip per scroll frame. Both wrapper features
		// that shape the range - the always-rendered indices and the directional overscan - are
		// therefore compiled into the single extractor upstream allows.
		const pinnedIndexes = Array.isArray(alwaysRenderedIndexes) ? alwaysRenderedIndexes : [];
		const hasDirectionalOverscan = Number.isFinite(overscanLeading) || Number.isFinite(overscanTrailing);

		if (pinnedIndexes.length > 0 || hasDirectionalOverscan) {
			resolved.rangeExtractor = (range) => {
				const windowIndexes = hasDirectionalOverscan
					? this.extractDirectionalRange(range, overscanLeading, overscanTrailing)
					: defaultRangeExtractor(range);

				if (pinnedIndexes.length === 0) {
					return windowIndexes;
				}

				const indexes = new Set([...pinnedIndexes, ...windowIndexes]);
				return [...indexes].filter(i => i >= 0 && i < range.count).sort((a, b) => a - b);
			};
		}

		return resolved;
	}

	/**
	 * defaultRangeExtractor, but spending more of the budget on the side the user is heading towards.
	 *
	 * Deliberately NOT gated on isScrolling, the way react-window's _getRangeToRender is. React
	 * re-renders when its isScrolling state flips, so its range genuinely recomputes at rest;
	 * virtual-core memoizes getVirtualIndexes on [rangeExtractor, overscan, count, startIndex,
	 * endIndex, lanes] and isScrolling is not among them, so an extractor that read it would keep
	 * returning the scrolling-time answer until the visible range moved again. The bias would then
	 * silently outlive the scroll - the opposite of what gating it was meant to achieve.
	 *
	 * The trailing count is what makes that safe: it is a floor held at all times, not a value that
	 * lapses. Leave it at overscan and reversing is never worse than a symmetric window; the leading
	 * side is then pure profit.
	 */
	extractDirectionalRange(range, leading, trailing) {
		// Counted in rows of `lanes` items, as defaultRangeExtractor has done since virtual-core
		// 3.19.0. Counting items would leave the edge rows partly rendered, buffering some lanes
		// less than others.
		const lanes = Math.max(range.lanes ?? 1, 1);
		const resolve = (value) => (Number.isFinite(value) ? value : range.overscan) * lanes;
		const leadingCount = resolve(leading);
		const trailingCount = resolve(trailing);

		// A list that has never scrolled reports no direction. Treating that as forward puts the
		// bigger buffer below, which is where a first scroll almost always goes.
		// virtual-core also computes a range from inside its own constructor, before the assignment
		// in ours has run, so this can genuinely be undefined on the very first call.
		const isBackward = this.virtualizer?.scrollDirection === 'backward';

		const start = Math.max(range.startIndex - (isBackward ? leadingCount : trailingCount), 0);
		const end = Math.min(range.endIndex + (isBackward ? trailingCount : leadingCount), range.count - 1);

		const indexes = [];
		for (let index = start; index <= end; index++) {
			indexes.push(index);
		}

		return indexes;
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

	/**
	 * Called from every ResizeObserver callback that can end in a notification. Blazor WebAssembly
	 * runs the .NET handler of invokeMethodAsync - and the render it queues - synchronously, so a
	 * notification sent from inside the callback rewrites the DOM in the middle of the browser's
	 * resize-observation step. Rows that render adds or resizes can only be observed a frame later,
	 * and the browser reports that as a window error, "ResizeObserver loop completed with
	 * undelivered notifications", which error trackers record. virtual-core 3.19 measures a whole
	 * batch before notifying, which made it far more frequent than under 3.17.
	 *
	 * The flag lasts until the callback returns - the microtask - and notifyChange sends anything
	 * raised meanwhile one task later, as a single notification.
	 */
	markInsideResizeObserver() {
		if (!this.insideResizeObserver) {
			this.insideResizeObserver = true;
			queueMicrotask(() => { this.insideResizeObserver = false; });
		}
	}

	notifyChange() {
		if (this.suppressNotifications) {
			return;
		}

		if (this.insideResizeObserver) {
			if (this.pendingNotification === null) {
				this.pendingNotification = setTimeout(() => {
					this.pendingNotification = null;
					this.notifyChange();
				}, 0);
			}
			return;
		}

		clearTimeout(this.pendingNotification);
		this.pendingNotification = null;

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
				const key = index >= 0 ? this.itemKeys[index] : undefined;
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

	setItems(newItemKeys, newItemSizes) {
		// Snapshot existing per-key measurements before measure() wipes them. An item-set change
		// (a filter swap, an inserted child row) must not cost the measurements of items whose
		// content did not change - otherwise expanding a group clears the group's measured size,
		// falls back to the estimate, and the inserted items land on top of the group's bottom edge
		// until the next measure pass.
		const sizeSnapshot = this.virtualizer.itemSizeCache instanceof Map
			? new Map(this.virtualizer.itemSizeCache)
			: new Map();

		this.itemKeys = newItemKeys ?? [];
		this.itemSizes = newItemSizes ?? [];

		// Drop measured-key tracking for keys no longer present. Their itemSizeCache entries are
		// simply not restored below, which evicts them.
		const newKeys = new Set(this.itemKeys);
		for (const key of [...this.measuredKeys]) {
			if (!newKeys.has(key)) {
				this.measuredKeys.delete(key);
			}
		}

		this.virtualizer.setOptions({
			...this.virtualizer.options,
			count: this.itemKeys.length,
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
		return this.runScrollCommand(() => this.virtualizer.scrollToIndex(index, { align, behavior }));
	}

	scrollToOffset(offset, align, behavior) {
		return this.runScrollCommand(() => this.virtualizer.scrollToOffset(offset, { align, behavior }));
	}

	scrollToEnd(behavior) {
		return this.runScrollCommand(() => this.virtualizer.scrollToEnd({ behavior }));
	}

	scrollBy(delta, behavior) {
		return this.runScrollCommand(() => this.virtualizer.scrollBy(delta, { behavior }));
	}

	/**
	 * Runs a scroll command, first stopping a touch fling that is still moving the list. Android
	 * Chrome carries a fling on after a programmatic scroll write, so a jump issued mid-fling lands
	 * wherever the momentum takes it instead of on its target. virtual-core stops a fling before a
	 * command only on iOS 27; this stands in for the rest until
	 * https://github.com/TanStack/virtual/pull/1347 ships, and goes away with it.
	 *
	 * Hiding the scroll axis's overflow for two frames is what stops the momentum - the same trick
	 * virtual-core uses on iOS 27. It runs on iOS too, where it is not needed (before 27 the write
	 * stops the fling itself, and on 27 virtual-core does) and costs those two frames: telling the
	 * platforms apart would take copying virtual-core's private user-agent checks. Its own
	 * _stopMomentum is not reused: it is private, and it drops the callback of a command superseded
	 * mid-stop, which would leave that command's caller awaiting forever. Without a touch there is
	 * no fling to stop, and on a desktop hiding the overflow would also hide a classic scrollbar and
	 * reflow the list, so the write goes out at once.
	 */
	async runScrollCommand(command) {
		const isTouchFling = this.lastInputWasTouch && !this.touchActive
			&& this.virtualizer.isScrolling === true;
		if (isTouchFling) {
			// Shared by every command issued during the same stop, so none of them reads back the
			// "hidden" another one set as the value to restore.
			this.momentumStop ??= this.stopMomentum()?.finally(() => { this.momentumStop = null; }) ?? null;
			if (this.momentumStop) {
				await this.momentumStop;
				if (!this.cleanupMount) {
					return;
				}
			}
		}

		command();
	}

	/** Hides the scroll axis's overflow for two frames; null when it had to be put back at once. */
	stopMomentum() {
		const element = this.isWindowScroller ? this.findScrollContainer(this.rootElement) : this.scrollContainer;
		const property = this.options.horizontal ? 'overflow-x' : 'overflow-y';
		const { style } = element;
		const value = style.getPropertyValue(property);
		const priority = style.getPropertyPriority(property);
		const crossSize = () => this.options.horizontal ? element.clientHeight : element.clientWidth;
		const crossSizeBefore = crossSize();
		style.setProperty(property, 'hidden', 'important');
		// A classic scrollbar takes layout space, so hiding it would reflow the list for two frames
		// (a touchscreen laptop). Put it back before anything paints and write at once, as before.
		if (crossSize() !== crossSizeBefore) {
			style.setProperty(property, value, priority);
			return null;
		}
		return nextFrame().then(nextFrame).then(() => style.setProperty(property, value, priority));
	}

	cancelScroll() {
		this.virtualizer.cancelScroll();
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
		this.containerObserver?.disconnect();
		clearTimeout(this.pendingNotification);
		for (const [type, listener] of Object.entries(this.inputListeners)) {
			window.removeEventListener(type, listener, { capture: true });
		}
		if (this.cleanupMount) {
			try {
				this.cleanupMount();
			} catch (error) {
				// Not rethrown: .NET is disposing the component and has nothing to do about it, and
				// the instance is abandoned either way. A warning rather than an error, because an
				// error here would reach the app's error tracker for a list that is already gone.
				console.warn('AdaptiveVirtualize: virtual-core teardown failed', error);
			}
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
