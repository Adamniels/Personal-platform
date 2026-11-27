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
import {
	amount,
	type RunningApi,
	type RunningList,
	type RunningRun,
} from "./running";

const NOW = Date.parse("2027-01-15T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const RUN = "22222222-2222-2222-2222-222222222222";
const OTHER = "33333333-3333-3333-3333-333333333333";
const LIST = "/devflow/api/running/events";
const LOG = `/devflow/api/projects/${PROJECT}/runs/${RUN}/log/events`;

function run(overrides: Partial<RunningRun> = {}): RunningRun {
	return {
		run_id: RUN,
		project_id: PROJECT,
		project_name: "Fodr",
		worker_id: "44444444-4444-4444-4444-444444444444",
		worker_name: "fodr-1",
		kind: "build",
		state: "running",
		stopping: false,
		card_number: 51,
		feature_number: null,
		title: "Share a plan by link",
		fix_round: null,
		model: "opus",
		// 14 minutes and 5 seconds before now.
		started_at: "2027-01-15T11:45:55+00:00",
		tokens: 120_000,
		cache_reads: 2_100_000,
		...overrides,
	};
}

function list(runs: RunningRun[]): RunningList {
	return {
		runs,
		busy_workers: new Set(runs.map((each) => each.worker_id)).size,
		next: [],
		next_more: 0,
	};
}

/** Dev flow at Running now, its list and streams the test's own, and the
 * clock at `clock.now`, which the test moves. */
function openRunning(
	first: RunningList,
	stopRun: RunningApi["stopRun"] = () => Promise.resolve(),
) {
	window.history.pushState(null, "", "/devflow/running");
	const events = fakeEvents();
	const clock = { now: NOW };
	const api: RunningApi = {
		loadRunning: () => Promise.resolve(first),
		stopRun,
		openEvents: events.open,
	};
	render(
		<App
			loadStatus={() => new Promise(() => {})}
			waiting={{
				loadWaiting: () => new Promise(() => {}),
				refresh: () => Promise.resolve(),
			}}
			running={api}
			now={() => clock.now}
		/>,
	);
	return { events, clock };
}

function tabNames() {
	return screen.getAllByRole("tab").map((tab) => tab.textContent);
}

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close. These stand in for the browser's, as claude-login.test.tsx's do.
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
	this.open = true;
	this.querySelector("button")?.focus();
};
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
	if (!this.open) return;
	this.open = false;
	this.dispatchEvent(new Event("close"));
};

afterEach(cleanup);

describe("Running now", () => {
	test("a panel shows the card, state, time, tokens, and the log as lines arrive", async () => {
		const { events, clock } = openRunning(list([run()]));

		const panel = await screen.findByRole("article", {
			name: "#51 Share a plan by link",
		});
		// The tab carries the number of busy workers.
		expect(tabNames()).toEqual([
			"Waiting for you",
			"Running now 1",
			"Projects",
		]);
		expect(within(panel).getByText("Building")).toBeTruthy();
		expect(within(panel).getByText("Fodr")).toBeTruthy();
		expect(within(panel).getByText(/fodr-1/)).toBeTruthy();
		expect(within(panel).getByText(/opus/)).toBeTruthy();
		expect(
			within(panel).getByText("120 k tokens · 2.1 M cache reads"),
		).toBeTruthy();
		expect(within(panel).getByText("14 min 5 s")).toBeTruthy();
		expect(
			within(panel)
				.getByRole("link", { name: "Open card" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${PROJECT}/cards/51`);

		// The time ticks each second.
		clock.now += 1000;
		await within(panel).findByText("14 min 6 s", {}, { timeout: 2500 });

		// The log, as its lines arrive, each as text, never as HTML.
		const log = events.at(LOG);
		log.send("line", { seq: 1, at: "", kind: "system", text: "Cloning" }, 1);
		log.send(
			"line",
			{ seq: 2, at: "", kind: "tool", text: "Read app/routes/plan.ts" },
			2,
		);
		log.send(
			"line",
			{ seq: 3, at: "", kind: "text", text: "<b>Writing</b> the test" },
			3,
		);
		log.send("line", { seq: 4, at: "", kind: "tool_result", text: "ok" }, 4);
		// A line the page already has, after a reconnect, is shown once.
		log.send("line", { seq: 2, at: "", kind: "tool", text: "Read again" }, 2);
		const lines = within(
			within(panel).getByRole("log", { name: "Log of #51" }),
		).getAllByRole("listitem");
		expect(lines.map((line) => line.textContent)).toEqual([
			"Cloning",
			"Read app/routes/plan.ts",
			"<b>Writing</b> the test",
			"ok",
		]);
		// A tool's name is bold.
		expect(within(lines[1] as HTMLElement).getByText("Read").tagName).toBe(
			"STRONG",
		);
		expect(panel.querySelector("b")).toBeNull();

		// A second run starts: the list's stream sends it, and it is a panel too.
		events.at(LIST).send(
			"running",
			list([
				run(),
				run({
					run_id: OTHER,
					worker_id: "55555555-5555-5555-5555-555555555555",
					worker_name: "platform-1",
					project_name: "Personal-platform",
					kind: "review",
					card_number: 235,
					title: "Decks and cards, the pages",
					tokens: 85_000,
					cache_reads: 0,
				}),
			]),
		);
		const reviewing = await screen.findByRole("article", {
			name: "#235 Decks and cards, the pages",
		});
		expect(within(reviewing).getByText("Reviewing")).toBeTruthy();
		expect(tabNames()).toEqual([
			"Waiting for you",
			"Running now 2",
			"Projects",
		]);

		// The first run ends: its log says so and its stream is closed.
		log.send("end", {});
		expect(log.closed).toBe(true);
	});

	test("Stop now asks, then stops", async () => {
		const stopRun = vi.fn<RunningApi["stopRun"]>(() => Promise.resolve());
		openRunning(list([run()]), stopRun);
		const panel = await screen.findByRole("article", {
			name: "#51 Share a plan by link",
		});

		// Cancelled, nothing is sent.
		fireEvent.click(within(panel).getByRole("button", { name: "Stop now" }));
		let dialog = await screen.findByRole("dialog", { name: "Stop this run?" });
		expect(
			within(dialog).getByText(
				"What it did is kept on its branch, and it waits for you.",
			),
		).toBeTruthy();
		fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(stopRun).not.toHaveBeenCalled();

		// Confirmed, the stop is posted and the panel says it is stopping.
		fireEvent.click(within(panel).getByRole("button", { name: "Stop now" }));
		dialog = await screen.findByRole("dialog", { name: "Stop this run?" });
		fireEvent.click(within(dialog).getByRole("button", { name: "Stop now" }));
		await within(panel).findByText("Stopping…");
		expect(stopRun).toHaveBeenCalledWith(PROJECT, RUN);
		expect(
			within(panel).queryByRole("button", { name: "Stop now" }),
		).toBeNull();
	});

	test("no busy worker says so", async () => {
		openRunning(list([]));

		expect(await screen.findByText("Nothing is running")).toBeTruthy();
		expect(screen.getByText("Your workers are waiting for work.")).toBeTruthy();
		expect(tabNames()).toEqual(["Waiting for you", "Running now", "Projects"]);
	});
});

describe("amount", () => {
	test("a count is short, and one that rounds to 1000 k reads 1 M", () => {
		expect(amount(950)).toBe("950");
		expect(amount(1500)).toBe("1.5 k");
		expect(amount(120_000)).toBe("120 k");
		expect(amount(999_499)).toBe("999 k");
		expect(amount(999_500)).toBe("1 M");
		expect(amount(2_100_000)).toBe("2.1 M");
	});
});
