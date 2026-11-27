// A stand in for the browser's EventSource, which jsdom has not: a test opens the page with it,
// then sends each event itself, as Dev flow's streams would. Only tests import it.
import { act } from "@testing-library/react";
import type { EventStream } from "./events";

type Listener = (event: Event) => void;

/** One stream the page opened, by its address. */
export class FakeEvents implements EventStream {
	readyState = 1;
	readonly url: string;
	private readonly listeners = new Map<string, Listener[]>();

	constructor(url: string) {
		this.url = url;
	}

	addEventListener(type: string, listener: Listener): void {
		this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
	}

	close(): void {
		this.readyState = 2;
	}

	get closed(): boolean {
		return this.readyState === 2;
	}

	/** Sends an event of `type`, its data as JSON and its id when given. */
	send(type: string, data: unknown, id?: number): void {
		const event = new MessageEvent(type, {
			data: JSON.stringify(data),
			lastEventId: id === undefined ? "" : String(id),
		});
		act(() => {
			for (const listener of this.listeners.get(type) ?? []) listener(event);
		});
	}

	/** The stream fails for good, as EventSource does on an answer that is
	 * not a stream, such as a 401. */
	fail(): void {
		this.readyState = 2;
		act(() => {
			for (const listener of this.listeners.get("error") ?? [])
				listener(new Event("error"));
		});
	}
}

/** Every stream the page opened, in order, and the way it opens one. */
export function fakeEvents() {
	const opened: FakeEvents[] = [];
	return {
		opened,
		open: (url: string) => {
			const stream = new FakeEvents(url);
			opened.push(stream);
			return stream;
		},
		/** The last stream opened at `url` that is still open. */
		at: (url: string) => {
			const found = opened.filter((each) => each.url === url && !each.closed);
			const last = found[found.length - 1];
			if (!last) throw new Error(`no open stream at ${url}`);
			return last;
		},
	};
}
