'use strict';

// Relative, so the module resolves correctly under any <base href>. An absolute /_content/... path
// breaks the moment the app is hosted from a sub-path.
import {
	Virtualizer,
	observeElementOffset,
	observeElementRect,
	elementScroll,
	measureElement,
} from './tanstack-virtual-core.js';

export function create(rootElement, dotNetRef, initialDescriptors, overscan, estimatedItemSize) {
	return new AdaptiveVirtualizeInstance(rootElement, dotNetRef, initialDescriptors, overscan, estimatedItemSize);
}

class AdaptiveVirtualizeInstance {
	constructor(rootElement, dotNetRef, initialDescriptors, overscan, estimatedItemSize) {
		this.rootElement = rootElement;
		this.dotNetRef = dotNetRef;
		this.descriptors = initialDescriptors;
		this.estimatedItemSize = estimatedItemSize;
		this.scrollContainer = this.findScrollContainer(rootElement);
		this.lastVirtualItems = null;
		this.lastTotalSize = -1;
		this.suppressNotifications = false;
		// Keys already measured during the current item-set lifetime. measureElement forces a
		// layout (getBoundingClientRect), which on iOS WKWebView is 10-50x more expensive than on
		// Chrome. Skipping already-measured keys drops a scroll-time measure pass from 100-1000 ms
		// to a few ms: only freshly virtualized rows pay the cost.
		this.measuredKeys = new Set();

		this.virtualizer = new Virtualizer({
			count: initialDescriptors.length,
			getScrollElement: () => this.scrollContainer,
			estimateSize: (index) => this.descriptors[index]?.estimatedSize ?? this.estimatedItemSize,
			getItemKey: (index) => this.descriptors[index]?.key ?? String(index),
			observeElementOffset,
			observeElementRect,
			scrollToFn: elementScroll,
			measureElement,
			overscan: overscan,
			onChange: () => this.notifyChange(),
		});

		this.virtualizer._willUpdate();
		this.cleanupMount = this.virtualizer._didMount();
		this.notifyChange();
	}

	findScrollContainer(element) {
		let current = element.parentElement;
		while (current) {
			const overflowY = window.getComputedStyle(current).overflowY;
			if (overflowY === 'auto' || overflowY === 'scroll') {
				return current;
			}
			current = current.parentElement;
		}
		return document.scrollingElement || document.documentElement;
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

		const payload = virtualItems.map(virtualItem => ({
			index: virtualItem.index,
			start: virtualItem.start,
			size: virtualItem.size,
			end: virtualItem.end,
			key: String(virtualItem.key),
		}));

		this.dotNetRef.invokeMethodAsync('OnVirtualItemsChanged', payload, totalSize);
	}

	areVirtualItemsEqual(a, b) {
		if (a === b) return true;
		if (!a || !b) return false;
		if (a.length !== b.length) return false;
		for (let i = 0; i < a.length; i++) {
			// The key must be compared too. When two item sets produce the same row geometry but a
			// different row at the same index, index/start/size all match while the row's identity
			// changed. Without the key check notifyChange bails, .NET keeps stale placements, and
			// the render loop skips the slot on the key mismatch until some later scroll happens to
			// produce different geometry.
			if (a[i].index !== b[i].index ||
				a[i].start !== b[i].start ||
				a[i].size !== b[i].size ||
				a[i].key !== b[i].key) {
				return false;
			}
		}
		return true;
	}

	measureRenderedRows() {
		const elements = this.rootElement.querySelectorAll('[data-index]');
		if (elements.length === 0) {
			return;
		}

		// Suppress per-element onChange so we don't fire one .NET round-trip per measured row. The
		// virtualizer calls onChange synchronously inside measureElement whenever a size differs
		// from cache, which is 5-10 callbacks per render pass - each one a separate Blazor render.
		// A single notify is flushed at the end instead.
		this.suppressNotifications = true;
		try {
			elements.forEach(el => {
				const indexAttr = el.getAttribute('data-index');
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
		// (a filter swap, an inserted child row) must not cost the measurements of rows whose
		// content did not change - otherwise expanding a group clears the group's measured height,
		// falls back to the estimate, and the inserted rows land on top of the group's bottom edge
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

		this.notifyChange();
	}

	/**
	 * Drops the cached measurement for one key. Used when a row's content changed shape while its
	 * key stayed the same (a group toggling collapsed/expanded changes its height by 100+px without
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

	dispose() {
		if (this.cleanupMount) {
			try { this.cleanupMount(); } catch { /* already torn down */ }
			this.cleanupMount = null;
		}
	}
}
