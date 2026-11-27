import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import { App } from "./app";
import { fakeEvents } from "./events.testing";
import { cardPath, featurePath } from "./sections";
import { duration, type UsageApi, type UsageView } from "./usage";

const PROJECT = "11111111-1111-1111-1111-111111111111";
const OTHER = "22222222-2222-2222-2222-222222222222";

/** 30 days ending 27 November 2026, each with the tokens `used` names for it
 * and 0 for any other, as the service sends them. */
function days(used: Record<string, number> = {}) {
	return Array.from({ length: 30 }, (_, back) => {
		const date = new Date(Date.UTC(2026, 10, 27 - 29 + back))
			.toISOString()
			.slice(0, 10);
		return { date, tokens: used[date] ?? 0 };
	});
}

function view(overrides: Partial<UsageView> = {}): UsageView {
	return {
		period: "30d",
		projects: [
			{
				id: PROJECT,
				name: "Personal-platform",
				runs: 9,
				agent_ms: 11_580_000,
				tokens: 9_250,
				cache_reads: 58_003,
				setting_up: {
					runs: 2,
					agent_ms: 1_860_000,
					tokens: 1_000,
					cache_reads: 0,
				},
				features: [
					{
						number: 10,
						title: "Usage and history",
						runs: 6,
						agent_ms: 6_120_000,
						elapsed_ms: 10_440_000,
						tokens: 3_250,
						cache_reads: 51_003,
						fix_rounds: 1,
						sent_back: 1,
						own: { runs: 3, agent_ms: 720_000, tokens: 150, cache_reads: 3 },
						cards: [
							{
								number: 11,
								title: "Add Tokens.Of",
								runs: 2,
								agent_ms: 3_600_000,
								elapsed_ms: 4_200_000,
								tokens: 2_100,
								cache_reads: 51_000,
								fix_rounds: 2,
								sent_back: 1,
							},
							{
								number: 12,
								title: "Add the tab",
								runs: 1,
								agent_ms: 1_800_000,
								elapsed_ms: 1_800_000,
								tokens: 1_000,
								cache_reads: 0,
								fix_rounds: 0,
								sent_back: 0,
							},
						],
					},
				],
				cards: [
					{
						number: 20,
						title: "Fix the nav",
						runs: 1,
						agent_ms: 3_600_000,
						elapsed_ms: 3_600_000,
						tokens: 5_000,
						cache_reads: 7_000,
						fix_rounds: 0,
						sent_back: 0,
					},
					{
						// A card Dev flow no longer holds has no title and no page.
						number: 21,
						title: null,
						runs: 1,
						agent_ms: 60_000,
						elapsed_ms: 60_000,
						tokens: 10,
						cache_reads: 0,
						fix_rounds: 0,
						sent_back: 0,
					},
				],
				days: days({
					"2026-10-29": 300,
					"2026-11-20": 24_358,
					"2026-11-26": 1_000,
				}),
			},
			{
				id: OTHER,
				name: "fodr",
				runs: 1,
				agent_ms: 3_600_000,
				tokens: 100,
				cache_reads: 0,
				setting_up: null,
				features: [],
				cards: [],
				days: days(),
			},
		],
		...overrides,
	};
}

function open(path: string, loadUsage: UsageApi["loadUsage"]) {
	window.history.pushState(null, "", path);
	render(
		<App
			loadStatus={() => new Promise(() => {})}
			waiting={{
				loadWaiting: () => new Promise(() => {}),
				refresh: () => Promise.resolve(),
			}}
			running={{
				loadRunning: () => new Promise(() => {}),
				stopRun: () => Promise.resolve(),
				openEvents: fakeEvents().open,
			}}
			usage={{ loadUsage }}
		/>,
	);
}

/** Each row of a table by its first cell's first line. */
function names(table: HTMLElement): string[] {
	return within(table)
		.getAllByRole("row")
		.slice(1)
		.map(
			(row) => row.querySelector("td")?.firstElementChild?.textContent ?? "",
		);
}

afterEach(() => {
	cleanup();
	window.history.pushState(null, "", "/");
});

describe("Usage and history", () => {
	test("is the last tab, at its own address", async () => {
		open("/devflow/usage", () => Promise.resolve(view()));
		const tabs = screen.getAllByRole("tab");
		expect(tabs.at(-1)?.textContent).toBe("Usage and history");
		expect(tabs.at(-1)?.getAttribute("aria-selected")).toBe("true");
		expect(
			await screen.findByRole("region", { name: "Usage of Personal-platform" }),
		).toBeTruthy();
	});

	test("projects open into features and cards", async () => {
		open("/devflow/usage", () => Promise.resolve(view()));

		// A table per project, most tokens first, each its own row with its
		// numbers, closed.
		const table = await screen.findByRole("region", {
			name: "Usage of Personal-platform",
		});
		const regions = screen
			.getAllByRole("region")
			.map((region) => region.getAttribute("aria-label"))
			.filter((label) => label?.startsWith("Usage of"));
		expect(regions).toEqual(["Usage of Personal-platform", "Usage of fodr"]);
		expect(names(table)).toEqual(["Personal-platform"]);
		const project = within(table).getAllByRole("row")[1] as HTMLElement;
		expect(project.textContent).toContain("9,250");
		expect(project.textContent).toContain("58,003");
		expect(project.textContent).toContain("3 h 13 min");

		// Opened, its features, its cards on their own and Setting up.
		const toggle = within(table).getByRole("button", {
			name: "Personal-platform",
		});
		expect(toggle.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(toggle);
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
		expect(names(table)).toEqual([
			"Personal-platform",
			"#10 Usage and history",
			"#20 Fix the nav",
			"#21",
			"Setting up",
		]);

		// The feature opens into its cards, by tokens, and its own runs.
		const feature = within(table).getByRole("button", {
			name: "Open #10 Usage and history",
		});
		fireEvent.click(feature);
		expect(names(table)).toEqual([
			"Personal-platform",
			"#10 Usage and history",
			"#11 Add Tokens.Of",
			"#12 Add the tab",
			"The feature's own runs",
			"#20 Fix the nav",
			"#21",
			"Setting up",
		]);
		const rows = within(table).getAllByRole("row");
		const tenth = rows[2] as HTMLElement;
		// Runs, agent time, elapsed, tokens, cache reads, fix rounds, sent back.
		expect(
			[...tenth.querySelectorAll("td")]
				.slice(1)
				.map((cell) => cell.textContent),
		).toEqual(["6", "1 h 42 min", "2 h 54 min", "3,250", "51,003", "1", "1"]);
		const own = rows[5] as HTMLElement;
		expect(
			[...own.querySelectorAll("td")].slice(1).map((cell) => cell.textContent),
		).toEqual(["3", "12 min", "", "150", "3", "", ""]);

		// Each card and the feature link to their pages; a card no longer held
		// does not.
		expect(
			within(table)
				.getByRole("link", { name: "Usage and history" })
				.getAttribute("href"),
		).toBe(featurePath(PROJECT, 10));
		expect(
			within(table)
				.getByRole("link", { name: "Add Tokens.Of" })
				.getAttribute("href"),
		).toBe(cardPath(PROJECT, 11));
		expect(within(table).getAllByRole("link")).toHaveLength(4);

		// Closed again, its cards go.
		fireEvent.click(
			within(table).getByRole("button", {
				name: "Close #10 Usage and history",
			}),
		);
		expect(names(table)).not.toContain("#11 Add Tokens.Of");
	});

	test("the period is in the address", async () => {
		const loadUsage = vi.fn((period: string) =>
			Promise.resolve(view({ period })),
		);
		open("/devflow/usage?period=7d", loadUsage);

		const select = (await screen.findByLabelText(
			"Period",
		)) as HTMLSelectElement;
		expect(select.value).toBe("7d");
		expect(
			[...select.options].map((option) => [option.value, option.text]),
		).toEqual([
			["7d", "Last 7 days"],
			["30d", "Last 30 days"],
			["all", "All time"],
		]);
		await waitFor(() => expect(loadUsage).toHaveBeenCalledWith("7d"));

		fireEvent.change(select, { target: { value: "all" } });
		expect(window.location.pathname).toBe("/devflow/usage");
		expect(window.location.search).toBe("?period=all");
		await waitFor(() => expect(loadUsage).toHaveBeenCalledWith("all"));
		expect(select.value).toBe("all");
	});

	test("30 days without a period, or with one it does not know", async () => {
		const loadUsage = vi.fn((period: string) =>
			Promise.resolve(view({ period })),
		);
		open("/devflow/usage?period=90d", loadUsage);

		const select = (await screen.findByLabelText(
			"Period",
		)) as HTMLSelectElement;
		expect(select.value).toBe("30d");
		await waitFor(() => expect(loadUsage).toHaveBeenCalledWith("30d"));
		expect(loadUsage).not.toHaveBeenCalledWith("90d");
	});

	test("nothing used says so", async () => {
		open("/devflow/usage?period=7d", (period) =>
			Promise.resolve(view({ period, projects: [] })),
		);
		expect(
			await screen.findByText("Nothing used in the last 7 days"),
		).toBeTruthy();
		expect(screen.queryByRole("table")).toBeNull();

		fireEvent.change(screen.getByLabelText("Period"), {
			target: { value: "all" },
		});
		expect(await screen.findByText("Nothing used yet")).toBeTruthy();
	});

	test("says when it could not be read", async () => {
		open("/devflow/usage", () => Promise.reject(new Error("down")));
		expect(
			await screen.findByText("Usage could not be read. Try again."),
		).toBeTruthy();
	});
});

describe("tokens per day", () => {
	test("each project shows 30 bars with the day's tokens on hover and focus, and a label for screen readers", async () => {
		open("/devflow/usage", () => Promise.resolve(view()));

		// The chart, under the project's table, named for screen readers with
		// the project, the span and its highest day.
		const chart = await screen.findByRole("img", {
			name: "Personal-platform: tokens per day, the last 30 days, most on 20 Nov, 24,358",
		});
		const figure = chart.closest("figure") as HTMLElement;
		const table = screen.getByRole("region", {
			name: "Usage of Personal-platform",
		});
		expect(
			table.compareDocumentPosition(figure) & Node.DOCUMENT_POSITION_FOLLOWING,
		).toBeTruthy();

		// 30 bars, one a day ending today, oldest first, each as high as its
		// tokens against the highest day's.
		const bars = [...chart.querySelectorAll<SVGElement>("[data-day]")];
		expect(bars).toHaveLength(30);
		expect(bars[0]?.dataset.day).toBe("2026-10-29");
		expect(bars.at(-1)?.dataset.day).toBe("2026-11-27");
		const height = (day: string) =>
			Number(
				chart
					.querySelector(`[data-day="${day}"] [data-bar]`)
					?.getAttribute("height") ?? 0,
			);
		expect(height("2026-11-20")).toBeGreaterThan(height("2026-11-26"));
		expect(height("2026-11-26")).toBeGreaterThan(0);
		expect(height("2026-11-27")).toBe(0);

		// The highest day's figure at the top, the first and last dates under
		// it, and no day's figure until one is pointed at or chosen.
		expect(within(figure).getByText("24,358")).toBeTruthy();
		expect(within(figure).getByText("29 Oct")).toBeTruthy();
		expect(within(figure).getByText("27 Nov")).toBeTruthy();
		const shown = () =>
			within(figure).queryByText(/^\d+ \w+: [\d,]+ tokens$/)?.textContent ??
			null;
		expect(shown()).toBeNull();

		// A day is chosen with a range input over the bars, a real control,
		// named for the project and saying the day it holds, today at first.
		const day = within(figure).getByRole("slider", {
			name: "A day of Personal-platform's tokens",
		}) as HTMLInputElement;
		expect([day.min, day.max, day.value]).toEqual(["0", "29", "29"]);
		expect(day.getAttribute("aria-valuetext")).toBe("27 Nov: 0 tokens");

		// On hover, the day under the pointer and its tokens; gone when the
		// pointer leaves.
		day.getBoundingClientRect = () => new DOMRect(0, 0, 300, 64);
		fireEvent.mouseMove(day, { clientX: 285 });
		expect(shown()).toBe("26 Nov: 1,000 tokens");
		expect(bars[28]?.dataset.active).toBe("true");
		fireEvent.mouseMove(day, { clientX: 5 });
		expect(shown()).toBe("29 Oct: 300 tokens");
		fireEvent.mouseLeave(day);
		expect(shown()).toBeNull();

		// On keyboard focus, the day it holds; the keys move it, as a browser
		// does on a range input, and leaving it clears the figure.
		fireEvent.focus(day);
		expect(shown()).toBe("27 Nov: 0 tokens");
		fireEvent.change(day, { target: { value: "28" } });
		expect(shown()).toBe("26 Nov: 1,000 tokens");
		expect(day.getAttribute("aria-valuetext")).toBe("26 Nov: 1,000 tokens");
		expect(bars[28]?.dataset.active).toBe("true");
		fireEvent.blur(day);
		expect(shown()).toBeNull();

		// A project with no tokens in the 30 days says so, its bars empty.
		const none = screen.getByRole("img", {
			name: "fodr: tokens per day, the last 30 days, none",
		});
		expect(none.querySelectorAll("[data-day]")).toHaveLength(30);
	});

	test("the chart is the last 30 days whatever the period", async () => {
		const loadUsage = vi.fn((period: string) =>
			Promise.resolve(view({ period })),
		);
		open("/devflow/usage?period=7d", loadUsage);
		const chart = await screen.findByRole("img", {
			name: /^Personal-platform: tokens per day, the last 30 days/,
		});
		expect(chart.querySelectorAll("[data-day]")).toHaveLength(30);
	});
});

describe("a duration", () => {
	test("reads in its two largest units", () => {
		expect(duration(0)).toBe("0 s");
		expect(duration(42_000)).toBe("42 s");
		expect(duration(60_000)).toBe("1 min");
		expect(duration(720_000)).toBe("12 min");
		expect(duration(6_120_000)).toBe("1 h 42 min");
		expect(duration(3_456_000_000)).toBe("40 d 0 h");
	});
});
