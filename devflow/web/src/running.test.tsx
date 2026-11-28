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
	type NextCard,
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

function list(
	runs: RunningRun[],
	next: NextCard[] = [],
	next_more = 0,
): RunningList {
	return {
		runs,
		busy_workers: new Set(runs.map((each) => each.worker_id)).size,
		next,
		next_more,
	};
}

const TICK = "55555555-5555-5555-5555-555555555555";

function nextCard(overrides: Partial<NextCard> = {}): NextCard {
	return {
		project_id: PROJECT,
		project_name: "Fodr",
		number: 52,
		title: "Revoke a shared link",
		state: "queued",
		job: "build",
		reason: "Waits for #51, which it depends on",
		link: "card",
		link_card: 51,
		feature: null,
		run_now: false,
		...overrides,
	};
}

/** The rows of "Next up", by their card's number and title. */
function nextRow(name: string): HTMLElement {
	const table = screen.getByRole("table", { name: "Next up" });
	const row = within(table)
		.getAllByRole("row")
		.find((each) => within(each).queryByRole("link", { name }));
	if (!row) throw new Error(`no row for ${name}`);
	return row;
}

/** The first "Run now anyway" in a row of "Next up": the reason is in its
 * column on a wide screen and under the title on a phone, both rendered. */
function runNowIn(name: string): HTMLElement {
	const [button] = within(nextRow(name)).getAllByRole("button", {
		name: "Run now anyway",
	});
	if (!button) throw new Error(`no Run now anyway in ${name}`);
	return button;
}

/** Every link in `row` named `name` goes to `href`: the reason is in its
 * column on a wide screen and under the title on a phone, one of them shown. */
function linksTo(row: HTMLElement, name: string, href: string) {
	const links = within(row).getAllByRole("link", { name });
	expect(links.length).toBeGreaterThan(0);
	for (const link of links) expect(link.getAttribute("href")).toBe(href);
}

/** Dev flow at Running now, its list and streams the test's own, and the
 * clock at `clock.now`, which the test moves. */
function openRunning(
	first: RunningList,
	stopRun: RunningApi["stopRun"] = () => Promise.resolve(),
	runNow: RunningApi["runNow"] = () => Promise.resolve(),
) {
	window.history.pushState(null, "", "/devflow/running");
	const events = fakeEvents();
	const clock = { now: NOW };
	const api: RunningApi = {
		loadRunning: () => Promise.resolve(first),
		stopRun,
		runNow,
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
			"Usage and history",
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
			"Usage and history",
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

	test("a card outside its schedule says when, with Edit schedules", async () => {
		openRunning(
			list(
				[run()],
				[
					nextCard({
						number: 60,
						title: "Night work",
						reason: "Outside its schedule, runs again Fri 22:00",
						link: "schedules",
						link_card: null,
					}),
					nextCard({
						number: 61,
						title: "Nothing on",
						reason: "No schedule is on",
						link: "schedules",
						link_card: null,
					}),
				],
			),
		);
		await screen.findByRole("article", { name: "#51 Share a plan by link" });

		// The sentence is said, and what to do is its own link, to the settings
		// page's card.
		for (const [row, reason] of [
			["#60 Night work", "Outside its schedule, runs again Fri 22:00"],
			["#61 Nothing on", "No schedule is on"],
		] as const) {
			const shown = nextRow(row);
			expect(within(shown).getAllByText(reason).length).toBeGreaterThan(0);
			expect(within(shown).queryByRole("link", { name: reason })).toBeNull();
			linksTo(
				shown,
				"Edit schedules",
				`/devflow/projects/${PROJECT}/settings#settings-schedules`,
			);
		}
	});

	test("a card outside its schedule offers run now anyway, for its feature or itself", async () => {
		const runNow = vi.fn<RunningApi["runNow"]>(() => Promise.resolve());
		openRunning(
			list(
				[run()],
				[
					nextCard({
						number: 60,
						title: "Night work",
						reason: "No schedule is on",
						link: "schedules",
						link_card: null,
						run_now: true,
					}),
					nextCard({
						number: 71,
						title: "A feature's card",
						reason: "No schedule is on",
						link: "schedules",
						link_card: null,
						feature: 70,
						run_now: true,
					}),
					nextCard(),
				],
			),
			undefined,
			runNow,
		);
		await screen.findByRole("article", { name: "#51 Share a plan by link" });

		// A card on its own runs itself now; a card of a feature, its feature.
		fireEvent.click(runNowIn("#60 Night work"));
		await waitFor(() =>
			expect(runNow).toHaveBeenCalledWith(PROJECT, "cards", 60),
		);
		expect(
			await screen.findByText("Running #60 outside the schedule"),
		).toBeTruthy();
		fireEvent.click(runNowIn("#71 A feature's card"));
		await waitFor(() =>
			expect(runNow).toHaveBeenCalledWith(PROJECT, "features", 70),
		);

		// A row with another reason offers nothing.
		expect(
			within(nextRow("#52 Revoke a shared link")).queryByRole("button", {
				name: "Run now anyway",
			}),
		).toBeNull();
	});

	test("next up lists cards with their reason and its link", async () => {
		const { events } = openRunning(
			list(
				[run()],
				[
					nextCard(),
					nextCard({
						project_id: OTHER,
						project_name: "Personal-platform",
						number: 236,
						title: "Scheduling",
						state: "reviewing",
						job: "review",
						reason: "Next for platform-1, after its current run",
						link: null,
						link_card: null,
					}),
					nextCard({
						project_id: TICK,
						project_name: "Tick",
						number: 9,
						title: "Sync conflict on wake",
						job: "read",
						reason: "No worker is running for Tick",
						link: "project_page",
						link_card: null,
					}),
					nextCard({
						number: 53,
						title: "Answer the questions",
						job: null,
						reason: "Waits for your answers on #50",
						link: "waiting_for_you",
						link_card: null,
					}),
					nextCard({
						project_id: OTHER,
						project_name: "Personal-platform",
						number: 240,
						title: "Fix the list",
						state: "fixing",
						job: "fix",
						reason: "No Claude login",
						link: "settings",
						link_card: null,
					}),
					nextCard({
						project_id: TICK,
						project_name: "Tick",
						number: 12,
						title: "Dark mode",
						reason: null,
						link: null,
						link_card: null,
					}),
				],
			),
		);

		// Under the panels, its heading with how many come next.
		await screen.findByRole("article", { name: "#51 Share a plan by link" });
		const heading = screen.getByRole("heading", { name: /^Next up/ });
		expect(heading.textContent).toBe("Next up 6");
		const table = screen.getByRole("table", { name: "Next up" });
		expect(
			within(table)
				.getAllByRole("columnheader")
				.map((each) => each.textContent),
		).toEqual(["Card", "Project", "State", "Why it is not running"]);

		// Each card, in the order given, linked to its page, with its project and
		// its state in words.
		const blocked = nextRow("#52 Revoke a shared link");
		linksTo(
			blocked,
			"#52 Revoke a shared link",
			`/devflow/projects/${PROJECT}/cards/52`,
		);
		expect(within(blocked).getAllByText("Fodr").length).toBeGreaterThan(0);
		expect(within(blocked).getAllByText("Queued").length).toBeGreaterThan(0);
		// Its reason, linked to where it is acted on: the card it waits for.
		linksTo(
			blocked,
			"Waits for #51, which it depends on",
			`/devflow/projects/${PROJECT}/cards/51`,
		);

		// A reason with no place to act is said, not linked.
		const busy = nextRow("#236 Scheduling");
		expect(within(busy).getAllByText("To review").length).toBeGreaterThan(0);
		expect(
			within(busy).getAllByText("Next for platform-1, after its current run")
				.length,
		).toBeGreaterThan(0);
		expect(
			within(busy).queryByRole("link", {
				name: "Next for platform-1, after its current run",
			}),
		).toBeNull();

		// The project's page, "Waiting for you" and the settings.
		const noWorker = nextRow("#9 Sync conflict on wake");
		expect(within(noWorker).getAllByText("To read").length).toBeGreaterThan(0);
		linksTo(
			noWorker,
			"No worker is running for Tick",
			`/devflow/projects/${TICK}`,
		);
		linksTo(
			nextRow("#53 Answer the questions"),
			"Waits for your answers on #50",
			"/devflow",
		);
		const login = nextRow("#240 Fix the list");
		expect(within(login).getAllByText("To fix").length).toBeGreaterThan(0);
		linksTo(login, "No Claude login", "/devflow/projects");

		// A card that runs as soon as a worker asks has no reason.
		expect(
			within(nextRow("#12 Dark mode")).getAllByText("Next to start").length,
		).toBeGreaterThan(0);

		// A reason that changes arrives with the list's stream.
		events
			.at(LIST)
			.send(
				"running",
				list(
					[run()],
					[nextCard({ reason: null, link: null, link_card: null })],
				),
			);
		await waitFor(() =>
			expect(
				within(nextRow("#52 Revoke a shared link")).getAllByText(
					"Next to start",
				).length,
			).toBeGreaterThan(0),
		);
		expect(screen.getByRole("heading", { name: /^Next up/ }).textContent).toBe(
			"Next up 1",
		);
	});

	test("a long list says how many more", async () => {
		const many = Array.from({ length: 50 }, (_, index) =>
			nextCard({ number: 100 + index, title: `Card ${100 + index}` }),
		);
		openRunning(list([], many, 7));

		const table = await screen.findByRole("table", { name: "Next up" });
		expect(within(table).getAllByRole("row")).toHaveLength(51);
		expect(screen.getByText("and 7 more")).toBeTruthy();
		// The heading counts every card that comes next.
		expect(screen.getByRole("heading", { name: /^Next up/ }).textContent).toBe(
			"Next up 57",
		);
		// With nothing running, the panels say so above it.
		expect(screen.getByText("Nothing is running")).toBeTruthy();
	});

	test("nothing next says so", async () => {
		openRunning(list([run()]));

		expect(await screen.findByText("Nothing waits to run.")).toBeTruthy();
		expect(screen.queryByRole("table", { name: "Next up" })).toBeNull();
	});

	test("no busy worker says so", async () => {
		openRunning(list([]));

		expect(await screen.findByText("Nothing is running")).toBeTruthy();
		expect(screen.getByText("Your workers are waiting for work.")).toBeTruthy();
		expect(tabNames()).toEqual([
			"Waiting for you",
			"Running now",
			"Projects",
			"Usage and history",
		]);
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
