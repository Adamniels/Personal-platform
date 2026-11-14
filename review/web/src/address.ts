// The address as the application's state, through the History API: a tab pushes a new entry, and
// Back and Forward come back as popstate. No router library, since review has one level of
// addresses. The memory centre's Brain/web/src/address.ts, written again here, since no service
// imports from another's directory.
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

/** The current query string, with its "?", re-rendering whoever reads it
 * when it changes, as a study session's filter does. */
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
