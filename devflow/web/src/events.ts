// The page's one way to open a server-sent events stream from Dev flow: the browser's EventSource,
// which sends the session cookie on the platform's one origin, opens the stream again by itself
// when it closes, and takes up a log from its Last-Event-ID. It cannot refresh a session, so a
// stream refused with a 401 closes for good, and its reader asks the client first, which does,
// then opens it again. See "The pages" in devflow/README.md. A test passes its own.

/** What the page needs of an EventSource. */
export type EventStream = {
	addEventListener(type: string, listener: (event: Event) => void): void;
	close(): void;
	/** 0 connecting, 1 open, 2 closed for good. */
	readonly readyState: number;
};

/** An EventSource's readyState once it will not open again by itself. */
export const CLOSED = 2;

/** How long a reader waits before opening a stream that closed for good
 * again. */
export const AGAIN = 2_000;

/** Opens the stream at `url`, the browser's EventSource. */
export function openEvents(url: string): EventStream {
	return new EventSource(url);
}

/** An event's data, which Dev flow sends as JSON. */
export function dataOf<T>(event: Event): T {
	return JSON.parse((event as MessageEvent<string>).data) as T;
}
