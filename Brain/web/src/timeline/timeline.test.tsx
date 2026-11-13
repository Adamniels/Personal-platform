import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { createClient } from "../api/client";
import { App } from "../app";
import { Providers } from "../providers";
import { fakeBrain } from "../test/fake-brain";

afterEach(cleanup);

const DAY = 24 * 60 * 60 * 1000;

function openTimeline(brain: ReturnType<typeof fakeBrain>) {
	window.history.pushState(null, "", "/brain/timeline");
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
	return within(screen.getByRole("tabpanel", { name: "Timeline" }));
}

function clock(iso: string): string {
	const at = new Date(iso);
	return `${String(at.getHours()).padStart(2, "0")}:${String(at.getMinutes()).padStart(2, "0")}`;
}

/** The entry whose line holds `text`. */
async function entry(timeline: ReturnType<typeof openTimeline>, text: string) {
	const line = await timeline.findByText(text, { exact: false });
	const found = line.closest("li");
	if (!found) throw new Error(`no entry holds ${text}`);
	return found;
}

function timeOf(entry: HTMLElement): string | null | undefined {
	return entry.querySelector("span[class*='time']")?.textContent;
}

function lineOf(entry: HTMLElement): string | null | undefined {
	return entry.querySelector("p")?.textContent;
}

function detailOf(entry: HTMLElement): string | null | undefined {
	return entry.querySelectorAll("p")[1]?.textContent;
}

describe("the timeline", () => {
	test("an event is one line of readable evidence with what, where from and when", async () => {
		const brain = fakeBrain();
		const own = brain.recorded(
			'Entered by hand as profile: "Lives in Uppsala"',
		);
		const sent = brain.recorded("Shown the article on index only scans.", {
			verb: "exposed",
			origin: "news",
			source: null,
			topic: {
				id: crypto.randomUUID(),
				slug: "postgres-indexing",
				name: "Postgres indexing",
			},
			payload: { rank: 3 },
		});
		const timeline = openTimeline(brain);

		const mine = await entry(timeline, "Lives in Uppsala");
		expect(lineOf(mine)).toBe(
			'You entered this Entered by hand as profile: "Lives in Uppsala"',
		);
		expect(timeOf(mine)).toBe(clock(own.recorded_at));
		expect(detailOf(mine)).toBe("Memory");

		const theirs = await entry(timeline, "index only scans");
		expect(lineOf(theirs)).toBe(
			"Exposed in news Shown the article on index only scans.",
		);
		expect(timeOf(theirs)).toBe(clock(sent.recorded_at));
		// Where it came from, and its subject as a badge.
		expect(detailOf(theirs)).toBe("newsPostgres indexing");
		expect(within(theirs).getByText("Postgres indexing").className).toMatch(
			/badge/,
		);
		// The payload is for features, never shown.
		expect(theirs.textContent).not.toContain("rank");
	});

	test("an event that happened long before it was recorded shows both times", async () => {
		const brain = fakeBrain();
		brain.recorded("Took a distributed systems course.", {
			occurred_at: "2021-01-01T00:00:00Z",
			occurred_precision: "year",
			recorded_at: "2026-11-12T12:00:00Z",
		});
		brain.recorded("Started the thesis.", {
			occurred_at: "2026-11-12T00:00:00Z",
			occurred_precision: "day",
			recorded_at: "2026-11-12T12:30:00Z",
		});
		const timeline = openTimeline(brain);

		const course = await entry(timeline, "distributed systems");
		expect(timeOf(course)).toBe("Happened 2021 · Recorded 12 Nov 2026");
		// The same day: one time alone, when it was recorded.
		const thesis = await entry(timeline, "Started the thesis");
		expect(timeOf(thesis)).toBe(clock("2026-11-12T12:30:00Z"));
	});

	test("events are grouped under the day they were recorded", async () => {
		const brain = fakeBrain();
		const now = Date.now();
		brain.recorded("Long ago.", {
			recorded_at: new Date(Date.UTC(2026, 9, 3, 12)).toISOString(),
		});
		brain.recorded("Yesterday's.", {
			recorded_at: new Date(now - DAY).toISOString(),
		});
		brain.recorded("Today's first.", {
			recorded_at: new Date(now - 1000).toISOString(),
		});
		brain.recorded("Today's second.", {
			recorded_at: new Date(now).toISOString(),
		});
		const timeline = openTimeline(brain);

		await timeline.findByText("Long ago.", { exact: false });
		const days = timeline.getAllByRole("heading", { level: 2 });
		expect(days.map((day) => day.textContent)).toEqual([
			"Today",
			"Yesterday",
			"3 Oct 2026",
		]);
		const lines = (name: string) =>
			within(timeline.getByRole("list", { name }))
				.getAllByRole("listitem")
				.map((item) => item.querySelector("p")?.textContent);
		expect(lines("Today")).toEqual([
			"You entered this Today's second.",
			"You entered this Today's first.",
		]);
		expect(lines("Yesterday")).toEqual(["You entered this Yesterday's."]);
		expect(lines("3 Oct 2026")).toEqual(["You entered this Long ago."]);
	});

	test("with no events yet it says what shows here", async () => {
		const timeline = openTimeline(fakeBrain());

		expect(await timeline.findByText("Nothing recorded yet")).toBeTruthy();
		expect(
			timeline.getByText("What you enter and correct shows here."),
		).toBeTruthy();
		expect(timeline.queryByRole("button")).toBeNull();
	});

	test("an event about an item, or that made one, links to its page in Everything", async () => {
		const brain = fakeBrain();
		const item = brain.add("profile", "Lives in Uppsala", "values");
		brain.recorded('Entered by hand as profile: "Lives in Uppsala"', {
			made_item: item.id,
		});
		brain.recorded("Retired by hand.", {
			verb: "retired",
			subject_item: item.id,
		});
		brain.recorded("Shown an article.", { verb: "exposed", origin: "news" });
		const timeline = openTimeline(brain);

		const entered = await timeline.findByRole("link", {
			name: /You entered this/,
		});
		expect(entered.getAttribute("href")).toBe(`/brain/everything/${item.id}`);
		const retired = timeline.getByRole("link", { name: /You retired this/ });
		expect(retired.getAttribute("href")).toBe(`/brain/everything/${item.id}`);
		// About no item, it links nowhere.
		const shown = await entry(timeline, "Shown an article.");
		expect(within(shown).queryByRole("link")).toBeNull();

		fireEvent.click(retired);
		expect(window.location.pathname).toBe(`/brain/everything/${item.id}`);
	});

	test("Show earlier loads the next fifty after the last one shown", async () => {
		const brain = fakeBrain();
		const start = Date.UTC(2026, 9, 1, 12);
		// 101 events a minute apart, then two recorded together across the
		// first page's end, as an edit records its two.
		for (let n = 0; n < 101; n += 1) {
			brain.recorded(`Event ${n}.`, {
				recorded_at: new Date(start + n * 60_000).toISOString(),
			});
		}
		const together = new Date(start + 52 * 60_000 - 30_000).toISOString();
		brain.recorded("Together, first.", {
			id: "00000000-0000-7000-8000-000000000002",
			recorded_at: together,
		});
		brain.recorded("Together, second.", {
			id: "00000000-0000-7000-8000-000000000001",
			recorded_at: together,
		});
		const timeline = openTimeline(brain);

		const shownLines = () =>
			timeline
				.getAllByRole("listitem")
				.map(
					(item) => item.querySelector("span[class*='evidence']")?.textContent,
				);
		await timeline.findByText("Event 100.");
		expect(shownLines()).toHaveLength(50);
		expect(shownLines().at(-1)).toBe("Together, first.");

		const more = timeline.getByRole("button", { name: "Show earlier" });
		fireEvent.click(more);
		await waitFor(() => expect(shownLines()).toHaveLength(100));
		// Nothing skipped and nothing twice: the tie went to the next page.
		expect(shownLines()[50]).toBe("Together, second.");
		expect(new Set(shownLines()).size).toBe(100);
		expect(brain.calls.at(-1)?.path).toMatch(
			/before=.+&before_id=00000000-0000-7000-8000-000000000002/,
		);
		// The place is kept: the first new entry has focus, the button stays.
		expect(document.activeElement?.textContent).toContain("Together, second.");

		fireEvent.click(timeline.getByRole("button", { name: "Show earlier" }));
		await waitFor(() => expect(shownLines()).toHaveLength(103));
		expect(shownLines().at(-1)).toBe("Event 0.");
		// Fewer than fifty came back, so there is nothing earlier.
		expect(timeline.queryByRole("button", { name: "Show earlier" })).toBeNull();
	});
});
