import { cleanup, render, screen, within } from "@testing-library/react";
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
});
