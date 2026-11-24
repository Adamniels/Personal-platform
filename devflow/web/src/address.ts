// The address as the application's state, through the History API: a tab pushes a new entry, and
// Back and Forward come back as popstate. No router library, since Dev flow has one level of
// addresses. Review's review/web/src/address.ts, written again here, since no service imports from
// another's directory.
import { useSyncExternalStore } from "react";

const listeners = new Set<() => void>();

function subscribe(listener: () => void) {
	listeners.add(listener);
	window.addEventListener("popstate", listener);
	return () => {
		listeners.delete(listener);
		window.removeEventListener("popstate", listener);
	};
}

/** The current path, re-rendering whoever reads it when it changes. */
export function usePathname(): string {
	return useSyncExternalStore(subscribe, () => window.location.pathname);
}

/** The current query string, such as "?item=…", re-rendering whoever reads
 * it when it changes. */
export function useSearch(): string {
	return useSyncExternalStore(subscribe, () => window.location.search);
}

/** Goes to a path within the application as a new history entry, or in
 * place of the current one with `replace`, for an address that has moved,
 * so Back does not return to it. */
export function navigate(path: string, { replace = false } = {}): void {
	if (path === window.location.pathname + window.location.search) return;
	if (replace) window.history.replaceState(null, "", path);
	else window.history.pushState(null, "", path);
	for (const listener of listeners) listener();
}

/** A click the page handles itself: the main button, with no key that asks
 * the browser for a new tab or window. */
export function plainClick(event: {
	button: number;
	metaKey: boolean;
	ctrlKey: boolean;
	shiftKey: boolean;
	altKey: boolean;
}): boolean {
	return (
		event.button === 0 &&
		!event.metaKey &&
		!event.ctrlKey &&
		!event.shiftKey &&
		!event.altKey
	);
}
