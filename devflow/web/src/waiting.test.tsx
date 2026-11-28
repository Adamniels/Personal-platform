import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import type { ActionsApi } from "./actions";
import { ApiError } from "./api/client";
import { App } from "./app";
import { choose, tiles } from "./inbox.testing";
import type { WaitingApi, WaitingItem, WaitingList } from "./waiting";

const NOW = Date.parse("2026-11-16T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const OTHER = "22222222-2222-2222-2222-222222222222";

function minutesAgo(minutes: number): string {
	return new Date(NOW - minutes * 60_000).toISOString();
}

function project(
	overrides: Partial<WaitingList["projects"][number]> = {},
): WaitingList["projects"][number] {
	return {
		id: PROJECT,
		owner: "Adamniels",
		name: "Personal-platform",
		last_success_at: minutesAgo(1),
		stale: false,
		failing: false,
		...overrides,
	};
}

function item(overrides: Partial<WaitingItem> = {}): WaitingItem {
	const number = overrides.number ?? 304;
	return {
		id: `00000000-0000-0000-0000-${String(number).padStart(12, "0")}`,
		project_id: PROJECT,
		project_name: "Personal-platform",
		number,
		kind: "waiting",
		sent_back: false,
		title: `Pull request ${number}`,
		web_url: `https://github.com/Adamniels/Personal-platform/pull/${number}`,
		slice_number: null,
		head_commit: "4".repeat(40),
		can_merge: true,
		summary: "Adds the vault.",
		reason: "High risk slice.",
		decisions: [],
		other_decisions: 0,
		reviewer_view: [],
		screenshots: [],
		read_at: minutesAgo(1),
		...overrides,
	};
}

function list(overrides: Partial<WaitingList> = {}): WaitingList {
	const items = overrides.items ?? [];
	return {
		count: items.filter((each) => !each.sent_back).length,
		needs_relink: false,
		stopped_cards: [],
		stopped_features: [],
		refused_cards: [],
		question_cards: [],
		failing_projects: [],
		setup_projects: [],
		setup_runs: [],
		features: [],
		items,
		projects: [project()],
		own_items: [],
		cards: [],
		decisions: [],
		...overrides,
	};
}

function openWith(waiting: WaitingApi, path = "/devflow") {
	window.history.pushState(null, "", path);
	render(
		<App
			loadStatus={() => new Promise(() => {})}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
			}}
			waiting={waiting}
			now={() => NOW}
		/>,
	);
}

function answering(answer: WaitingList): WaitingApi {
	return {
		loadWaiting: () => Promise.resolve(answer),
		refresh: () => Promise.resolve(),
	};
}

function panel() {
	return within(screen.getByRole("tabpanel"));
}

/** The item's card, by its title, chosen in the queue. */
async function card(title: string) {
	return within(await choose(title));
}

/** A group's rows, each its title and its meta line. */
function rows(group: HTMLElement) {
	return within(group)
		.getAllByRole("link")
		.map((row) => row.textContent);
}

afterEach(cleanup);

describe("the list and its groups", () => {
	test("the groups show with their counts, in order", async () => {
		openWith(
			answering(
				list({
					items: [
						item({ number: 304, kind: "waiting" }),
						item({ number: 305, kind: "waiting" }),
						item({ number: 306, kind: "stopped", can_merge: false }),
					],
				}),
			),
		);

		const waiting = await panel().findByRole("region", {
			name: "Pull requests waiting",
		});
		expect(within(waiting).getByText("2")).toBeTruthy();
		const stopped = panel().getByRole("region", { name: "Stopped" });
		expect(within(stopped).getByText("1")).toBeTruthy();
		expect(
			panel()
				.getAllByRole("region")
				.map((region) => region.getAttribute("aria-labelledby"))
				.map((id) => document.getElementById(id ?? "")?.textContent),
		).toEqual(["Pull requests waiting", "Stopped"]);
		expect(within(waiting).getAllByRole("link")).toHaveLength(2);
		expect(within(stopped).getAllByRole("link")).toHaveLength(1);
	});

	test("refused cards are a group with what is missing", async () => {
		openWith(
			answering(
				list({
					count: 2,
					stopped_cards: [
						{
							project_id: PROJECT,
							project_name: "Personal-platform",
							owner: "Adamniels",
							number: 7,
							title: "Count the habits",
							web_url:
								"https://github.com/Adamniels/Personal-platform/issues/7",
							reason: "The run failed: it reached its time limit",
							stopped_at: minutesAgo(30),
							pull_request_url: null,
							stopped_by_owner: false,
							feature_number: null,
						},
					],
					refused_cards: [
						{
							project_id: PROJECT,
							project_name: "Personal-platform",
							owner: "Adamniels",
							number: 4,
							title: "Add the empty state",
							web_url:
								"https://github.com/Adamniels/Personal-platform/issues/4",
							missing: ["Which page shows the list", "What an empty list says"],
							refused_at: minutesAgo(5),
						},
					],
				}),
			),
		);

		// A group of its own after Stopped, counted, one entry per card.
		const refused = await panel().findByRole("region", { name: "Refused" });
		expect(within(refused).getByText("1")).toBeTruthy();
		expect(
			panel()
				.getAllByRole("region")
				.map((region) => region.getAttribute("aria-labelledby"))
				.map((id) => document.getElementById(id ?? "")?.textContent),
		).toEqual(["Stopped", "Refused"]);
		expect(within(refused).getAllByRole("link")).toHaveLength(1);
		const entry = within(await choose("Add the empty state"));
		expect(entry.getByText("Refused", { selector: "span" })).toBeTruthy();
		expect(entry.getByText("Adamniels/Personal-platform")).toBeTruthy();
		expect(entry.getByText("card #4")).toBeTruthy();

		// What is missing, each a line of its own.
		const missing = entry.getByRole("list", { name: "What is missing" });
		expect(
			within(missing)
				.getAllByRole("listitem")
				.map((line) => line.textContent),
		).toEqual(["Which page shows the list", "What an empty list says"]);

		// The answer is on GitHub: a link to the issue, and nothing to do here.
		const open = entry.getByRole("link", { name: "Open on GitHub" });
		expect(open.getAttribute("href")).toBe(
			"https://github.com/Adamniels/Personal-platform/issues/4",
		);
		expect(open.getAttribute("target")).toBe("_blank");
		expect(entry.queryAllByRole("button")).toHaveLength(0);
		expect(
			entry.getByText(
				"Edit the card or comment on it on GitHub, and it is read again.",
			),
		).toBeTruthy();
	});

	test("a project whose setup failed its check is listed under Setup", async () => {
		openWith(
			answering(
				list({
					count: 1,
					setup_projects: [
						{
							project_id: PROJECT,
							project_name: "fodr",
							owner: "Adamniels",
							state: "setting_up",
							version: "4f6c1c36b0bd2c24e3d5a8f5d8c0e0a3a0e3b9a1",
							exit_code: 1,
							checked_at: minutesAgo(5),
						},
					],
				}),
			),
		);

		const group = await screen.findByRole("region", { name: "Setup" });
		expect(within(group).getAllByRole("link")).toHaveLength(1);
		const item = await choose("fodr's setup failed its check");
		expect(within(item).getByText("Check failed")).toBeTruthy();
		expect(within(item).getByText(/· exit 1 ·/)).toBeTruthy();
		expect(
			within(item)
				.getByRole("link", { name: "See its output on the project's page" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${PROJECT}`);
		expect(screen.queryByText("Nothing waits for you")).toBeNull();
	});

	test("a project whose setup run waits for me is listed under Setup", async () => {
		openWith(
			answering(
				list({
					count: 1,
					setup_runs: [
						{
							project_id: PROJECT,
							project_name: "fodr",
							owner: "Adamniels",
							state: "waiting_for_owner",
							reason: "Approved; it waits for you to merge it",
							pull_request_number: 7,
							pull_request_url: "https://github.com/Adamniels/fodr/pull/7",
							head_commit: "4f6c1c36b0bd2c24e3d5a8f5d8c0e0a3a0e3b9a1",
							changed_at: minutesAgo(5),
						},
					],
				}),
			),
		);

		const group = await screen.findByRole("region", { name: "Setup" });
		expect(within(group).getAllByRole("link")).toHaveLength(1);
		const item = await choose("fodr's setup waits for you");
		// Its status label, as a runner item's, and the words read out for the
		// accent border, once.
		expect(within(item).getByText("Waiting for you")).toBeTruthy();
		expect(within(item).getAllByText("Needs you")).toHaveLength(1);
		expect(
			within(item).getByText("Approved; it waits for you to merge it."),
		).toBeTruthy();
		expect(
			within(item)
				.getByRole("link", { name: "Merge it from the project's page" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${PROJECT}`);
	});

	test("an empty group is not shown", async () => {
		openWith(answering(list({ items: [item({ kind: "stopped" })] })));

		expect(
			await panel().findByRole("region", { name: "Stopped" }),
		).toBeTruthy();
		expect(
			panel().queryByRole("region", { name: "Pull requests waiting" }),
		).toBeNull();
		expect(
			panel().queryByRole("region", { name: "Sent back, the runner has it" }),
		).toBeNull();
	});

	test("within a group, items are by project name and then number", async () => {
		openWith(
			answering(
				list({
					projects: [
						project(),
						project({ id: OTHER, owner: "Adamniels", name: "Fodr" }),
					],
					items: [
						item({ number: 12, title: "Platform twelve" }),
						item({ number: 3, title: "Platform three" }),
						item({
							number: 40,
							title: "Fodr forty",
							project_id: OTHER,
							project_name: "Fodr",
						}),
					],
				}),
			),
		);

		const waiting = await panel().findByRole("region", {
			name: "Pull requests waiting",
		});
		expect(rows(waiting)).toEqual([
			"Fodr forty Fodr · #40",
			"Platform three Personal-platform · #3",
			"Platform twelve Personal-platform · #12",
		]);
	});

	test("an item's first line holds the project, its number, its title and its status", async () => {
		openWith(
			answering(list({ items: [item({ number: 304, title: "The vault" })] })),
		);

		const vault = await card("The vault");
		expect(vault.getByText("Adamniels/Personal-platform")).toBeTruthy();
		expect(vault.getByText("pull request #304")).toBeTruthy();
		expect(vault.getByText("Waiting for you")).toBeTruthy();
		expect(vault.getByText("Adds the vault.")).toBeTruthy();
	});

	test("a waiting item has the attention outline, a stopped one does not", async () => {
		openWith(
			answering(
				list({
					items: [
						item({ number: 1, title: "Waits" }),
						item({ number: 2, title: "Stops", kind: "stopped" }),
					],
				}),
			),
		);

		expect((await card("Waits")).getByText("Needs you")).toBeTruthy();
		expect((await card("Stops")).queryByText("Needs you")).toBeNull();
		// Its status label, and its tile of why it waits.
		expect((await card("Stops")).getAllByText("Stopped")).toHaveLength(2);
	});

	test("Open on GitHub links to the item in a new tab, without a referrer", async () => {
		openWith(answering(list({ items: [item({ title: "The vault" })] })));

		const open = (await card("The vault")).getByRole("link", {
			name: "Open on GitHub",
		});
		expect(open.getAttribute("href")).toBe(
			"https://github.com/Adamniels/Personal-platform/pull/304",
		);
		expect(open.getAttribute("target")).toBe("_blank");
		expect(open.getAttribute("rel")).toBe("noreferrer");
	});

	test("with nothing waiting, the tab has no count", async () => {
		openWith(answering(list({ count: 0 })));

		expect(await panel().findByText("Nothing waits for you")).toBeTruthy();
		expect(
			screen.getByRole("tab", { name: /Waiting for you/ }).textContent,
		).toBe("Waiting for you");
	});

	test("an address that is not https is not linked", async () => {
		openWith(
			answering(
				list({
					items: [item({ title: "Odd", web_url: "javascript:alert(1)" })],
				}),
			),
		);

		expect(
			(await card("Odd")).queryByRole("link", { name: "Open on GitHub" }),
		).toBeNull();
	});

	test("the tab's count is the number from the answer", async () => {
		// The answer counts the relink as well, so it is not the items' count.
		openWith(
			answering(list({ count: 3, needs_relink: true, items: [item()] })),
		);

		const tab = screen.getByRole("tab", { name: /Waiting for you/ });
		await waitFor(() => expect(tab.textContent).toBe("Waiting for you 3"));
	});
});

describe("decisions, the reviewer's view and the reasons", () => {
	test("an item with decisions shows each one's choice, alternative and reason, and the reviewer's view under them", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							title: "The vault",
							decisions: [
								{
									chosen: "A deck id the user does not have answers 422.",
									alternative: "404 everywhere.",
									why: "The card's acceptance said both.",
								},
								{
									chosen: "References carry the user id.",
									alternative: null,
									why: null,
								},
							],
							reviewer_view: ["Agrees, it matches the brain.", "Fine."],
						}),
					],
				}),
			),
		);

		const vault = await card("The vault");
		const decided = within(
			vault.getByRole("list", { name: "The agent decided" }),
		);
		expect(
			decided.getAllByRole("listitem").map((each) => each.textContent),
		).toEqual([
			"A deck id the user does not have answers 422.Instead of 404 everywhere.Because The card's acceptance said both.",
			"References carry the user id.",
		]);
		const reviewer = within(vault.getByRole("list", { name: "The reviewer" }));
		expect(
			reviewer.getAllByRole("listitem").map((point) => point.textContent),
		).toEqual(["Agrees, it matches the brain.", "Fine."]);
		// The runner's reason follows the tiles, in its own words.
		expect(vault.getByText("In the runner's words")).toBeTruthy();
	});

	test("decisions not marked for the owner are a muted line, with nothing to expand", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							title: "The vault",
							decisions: [{ chosen: "One.", alternative: null, why: null }],
							other_decisions: 2,
						}),
					],
				}),
			),
		);

		const vault = await card("The vault");
		expect(vault.getByText("and 2 more it did not need you for")).toBeTruthy();
		// Nothing to expand: the item's only buttons are its actions.
		expect(
			vault.queryAllByRole("button").map((button) => button.textContent),
		).toEqual(["Merge", "Send back"]);
	});

	test("without a reviewer's view, there is no reviewer block", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							title: "The vault",
							decisions: [{ chosen: "One.", alternative: null, why: null }],
						}),
					],
				}),
			),
		);

		expect(
			(await card("The vault")).queryByRole("list", { name: "The reviewer" }),
		).toBeNull();
	});

	test("an item without decisions shows the reason it waits", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							title: "The vault",
							reason: "High risk slice. Touches paths that steer the agents.",
							other_decisions: 1,
						}),
					],
				}),
			),
		);

		const vault = await card("The vault");
		expect(vault.getByText("In the runner's words")).toBeTruthy();
		expect(
			vault.getByText("High risk slice. Touches paths that steer the agents."),
		).toBeTruthy();
		expect(vault.queryByRole("list", { name: "The agent decided" })).toBeNull();
	});

	test("a stopped item shows the runner's reason", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							title: "Import a photo",
							kind: "stopped",
							can_merge: false,
							reason: "The slice is Blocked. The tests still fail.",
						}),
					],
				}),
			),
		);

		const stopped = await card("Import a photo");
		expect(stopped.getByText("Why it stopped")).toBeTruthy();
		expect(
			stopped.getByText("The slice is Blocked. The tests still fail."),
		).toBeTruthy();
		expect(stopped.queryByText("In the runner's words")).toBeNull();
	});

	test("sent back items are in their own muted group and are not counted", async () => {
		openWith(
			answering(
				list({
					count: 1,
					items: [
						item({ number: 1, title: "Waits" }),
						item({ number: 2, title: "Went back", sent_back: true }),
						item({
							number: 3,
							title: "Stopped and went back",
							kind: "stopped",
							sent_back: true,
						}),
					],
				}),
			),
		);

		const sentBack = await panel().findByRole("region", {
			name: "Sent back, the runner has it",
		});
		expect(rows(sentBack)).toEqual([
			"Went back Personal-platform · #2 · sent back",
			"Stopped and went back Personal-platform · #3 · sent back",
		]);
		const waiting = panel().getByRole("region", {
			name: "Pull requests waiting",
		});
		expect(within(waiting).getAllByRole("link")).toHaveLength(1);
		expect(within(waiting).getByText("1")).toBeTruthy();
		expect(panel().queryByRole("region", { name: "Stopped" })).toBeNull();
		const wentBack = await card("Went back");
		// Its status label, and its tile of why it waits.
		expect(wentBack.getAllByText("Sent back")).toHaveLength(2);
		expect(wentBack.queryByText("Needs you")).toBeNull();
		await waitFor(() =>
			expect(
				screen.getByRole("tab", { name: /Waiting for you/ }).textContent,
			).toBe("Waiting for you 1"),
		);
	});
});

describe("an item's detail, by kind", () => {
	test("a runner item shows why it waits, the reviewer and the decisions as tiles", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							number: 304,
							title: "The vault",
							decisions: [
								{
									chosen: "AES-GCM",
									alternative: "libsodium",
									why: "No new dependency",
								},
								{ chosen: "Read the key once", alternative: null, why: null },
							],
							other_decisions: 1,
							reviewer_view: [
								"Agrees.",
								"The key's file mode is worth a look.",
							],
						}),
						item({
							number: 306,
							title: "Import a photo",
							kind: "stopped",
							can_merge: false,
						}),
					],
				}),
			),
		);

		const vault = await choose("The vault");
		// The where line: the repository, the pull request and its commit.
		const where = within(vault).getByText("Adamniels/Personal-platform")
			.parentElement as HTMLElement;
		expect(where.textContent).toBe(
			"Adamniels/Personal-platform · pull request #304 · 4444444",
		);
		expect(
			within(vault).getByRole("heading", { level: 2, name: "The vault" }),
		).toBeTruthy();
		expect(tiles(vault)).toEqual([
			["Why it waits", "Your decision", "ci-ok and review-ok green"],
			["The reviewer", "Approved", "on 4444444 · 2 points for you"],
			["Decisions", "2 for you", "+1 it did not need you for"],
		]);
		// The decisions and the reviewer's points follow the tiles as insets.
		expect(
			within(vault).getByRole("list", { name: "The agent decided" }),
		).toBeTruthy();
		expect(
			within(vault).getByRole("list", { name: "The reviewer" }),
		).toBeTruthy();

		const stopped = await choose("Import a photo");
		// A stopped item carries no verdict, and the runner stops approved heads
		// too, such as one whose ci-ok failed, so the tile claims none.
		expect(tiles(stopped)).toEqual([
			["Why it waits", "Stopped", "the runner gave up"],
			["The reviewer", "Not known", "on 4444444 · not read once stopped"],
			["Decisions", "None for you", "none it decided alone"],
		]);
		expect(within(stopped).getByText("Not known").className).toMatch(/_idle_/);
		expect(within(stopped).getByText("High risk slice.")).toBeTruthy();
	});

	test("a setup item shows its facts and links the project's page", async () => {
		const checkedAt = minutesAgo(5);
		openWith(
			answering(
				list({
					count: 2,
					setup_projects: [
						{
							project_id: PROJECT,
							project_name: "fodr",
							owner: "Adamniels",
							state: "setting_up",
							version: "4f6c1c36b0bd2c24e3d5a8f5d8c0e0a3a0e3b9a1",
							exit_code: 1,
							checked_at: checkedAt,
						},
					],
					setup_runs: [
						{
							project_id: OTHER,
							project_name: "habits",
							owner: "Adamniels",
							state: "waiting_for_owner",
							reason: "Approved; it waits for you to merge it",
							pull_request_number: 7,
							pull_request_url: "https://github.com/Adamniels/habits/pull/7",
							head_commit: "5e7d2d47c1ce3d35f4e6b9a6e9d1f1b4b1f4c0b2",
							changed_at: minutesAgo(5),
						},
					],
				}),
			),
		);

		// A failed check: its version, its exit code and its time, and its
		// link to the project's page; no button.
		const check = await choose("fodr's setup failed its check");
		const [tile] = tiles(check);
		expect(tile?.slice(0, 2)).toEqual(["The check", "Failed"]);
		expect(tile?.[2]).toMatch(/^version 4f6c1c3 · exit 1 · /);
		expect(check.querySelector("time")?.getAttribute("datetime")).toBe(
			checkedAt,
		);
		expect(
			within(check)
				.getByRole("link", { name: "See its output on the project's page" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${PROJECT}`);
		expect(within(check).queryAllByRole("button")).toEqual([]);

		// A setup run: its state and its reason, and its link; no button.
		const run = await choose("habits's setup waits for you");
		expect(tiles(run)).toEqual([
			["The setup run", "Waits for you", "pull request #7 · 5e7d2d4"],
		]);
		expect(
			within(run).getByText("Approved; it waits for you to merge it."),
		).toBeTruthy();
		expect(
			within(run)
				.getByRole("link", { name: "Merge it from the project's page" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${OTHER}`);
		expect(within(run).queryAllByRole("button")).toEqual([]);
	});

	test("a budget reached shows under Stopped with Raise the budget", async () => {
		const landed = `ab12cd34${"0".repeat(32)}`;
		openWith(
			answering(
				list({
					count: 3,
					own_items: [
						{
							kind: "project_stopped",
							project_id: OTHER,
							project_name: "habits",
							owner: "Adamniels",
							branch: "main",
							activity_type: "push",
							actor: "someone",
							commit: landed,
							commit_url: `https://github.com/Adamniels/habits/commit/${landed}`,
							landed_at: minutesAgo(30),
							stopped_at: minutesAgo(29),
						},
						{
							kind: "budget_reached",
							project_id: PROJECT,
							project_name: "fodr",
							owner: "Adamniels",
							used: 1_234_000,
							budget: 1_000_000,
							lifts_at: "2026-11-17T00:00:00+01:00",
							time_zone: "Europe/Stockholm",
						},
					],
					failing_projects: [
						{
							project_id: OTHER,
							project_name: "habits",
							owner: "Adamniels",
							message:
								"Dev flow cannot run habits's cards: The network was away",
							reason: "The network was away",
							failures: 6,
							since: minutesAgo(60),
							wait_until: minutesAgo(-10),
						},
					],
				}),
			),
		);

		// Under Stopped, after the stopped and the failing projects, with what it
		// used of what as its meta line.
		const stopped = await panel().findByRole("region", { name: "Stopped" });
		const listed = rows(stopped);
		expect(listed).toHaveLength(3);
		expect(listed[0]).toContain("Something landed on main");
		expect(listed[1]).toContain("cannot run");
		expect(listed[2]).toContain("Adamniels/fodr reached its daily budget");
		expect(listed[2]).toContain("1.2 M of 1 M tokens");

		// Its detail says until when, as wall clock in the owner's zone, and that
		// the run that went over finished; Raise the budget opens the settings
		// page at the field. It acts nowhere in the inbox.
		const reached = await choose("Adamniels/fodr reached its daily budget");
		expect(
			within(reached).getByText(
				"Nothing new starts until Tue 00:00, midnight in Europe/Stockholm. The run that went over finished.",
			),
		).toBeTruthy();
		expect(
			within(reached)
				.getByRole("link", { name: "Raise the budget" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${PROJECT}/settings#usage`);
		expect(within(reached).queryAllByRole("button")).toEqual([]);
		expect(window.location.search).toBe(`?item=budget/${PROJECT}`);
	});
});

describe("the age of the picture, Refresh and the empty states", () => {
	test("every project fine: one muted line from the oldest success", async () => {
		openWith(
			answering(
				list({
					projects: [
						project({ last_success_at: minutesAgo(1) }),
						project({
							id: OTHER,
							name: "Fodr",
							last_success_at: minutesAgo(3),
						}),
					],
					items: [item()],
				}),
			),
		);

		expect(await panel().findByText("Updated 3 minutes ago")).toBeTruthy();
	});

	test("a stale project says how long it has not been updated", async () => {
		openWith(
			answering(
				list({
					projects: [
						project({ last_success_at: minutesAgo(125), stale: true }),
					],
					items: [item()],
				}),
			),
		);

		expect(
			await panel().findByText(
				"Adamniels/Personal-platform has not been updated for 2 hours",
			),
		).toBeTruthy();
		expect(panel().queryByText(/^Updated /)).toBeNull();
	});

	test("a project never read says so", async () => {
		openWith(
			answering(
				list({
					projects: [project({ last_success_at: null, stale: true })],
				}),
			),
		);

		expect(
			await panel().findByText(
				"Adamniels/Personal-platform has not been read yet",
			),
		).toBeTruthy();
	});

	test("a failing project says GitHub could not be read", async () => {
		openWith(
			answering(
				list({
					projects: [project({ failing: true })],
					items: [item()],
				}),
			),
		);

		expect(
			await panel().findByText(
				"Adamniels/Personal-platform: GitHub could not be read, showing what was last seen",
			),
		).toBeTruthy();
	});

	test("Refresh reads every project again and shows the new list", async () => {
		const refreshed: string[] = [];
		let answer = list({
			projects: [project(), project({ id: OTHER, name: "Fodr" })],
			items: [item({ title: "Before" })],
		});
		const load = vi.fn(() => Promise.resolve(answer));
		openWith({
			loadWaiting: load,
			refresh: (id) => {
				refreshed.push(id);
				answer = list({
					projects: answer.projects,
					items: [item({ title: "After" })],
				});
				return Promise.resolve();
			},
		});
		expect(await card("Before")).toBeTruthy();

		fireEvent.click(panel().getByRole("button", { name: "Refresh" }));

		expect(await card("After")).toBeTruthy();
		expect(panel().queryByRole("article", { name: "Before" })).toBeNull();
		expect(refreshed.sort()).toEqual([PROJECT, OTHER].sort());
	});

	test("an expired link says so at the top, with a link to Projects", async () => {
		openWith(answering(list({ needs_relink: true, count: 1 })));

		const line = await panel().findByText(
			"Your GitHub link has expired. You can read, but not merge or comment.",
		);
		expect(line).toBeTruthy();
		const link = panel().getByRole("link", { name: "Go to Projects" });
		expect(link.getAttribute("href")).toBe("/devflow/projects");
	});

	test("with nothing waiting and a project, nothing waits for you", async () => {
		openWith(answering(list({ items: [] })));

		expect(await panel().findByText("Nothing waits for you")).toBeTruthy();
		expect(
			panel().getByText("Your agents have nothing for you to decide."),
		).toBeTruthy();
	});

	test("with no project, the line points at Projects", async () => {
		openWith(answering(list({ projects: [], items: [] })));

		expect(await panel().findByText("Nothing waits for you")).toBeTruthy();
		expect(
			panel().getByText(
				"Connect a repository under Projects to see what waits for you.",
			),
		).toBeTruthy();
		expect(panel().queryByRole("button", { name: "Refresh" })).toBeNull();
	});

	test("a list that cannot be read says so", async () => {
		openWith({
			loadWaiting: () => Promise.reject(new Error("down")),
			refresh: () => Promise.resolve(),
		});

		expect((await panel().findByRole("alert")).textContent).toBe(
			"What waits for you could not be read. Try again.",
		);
	});
});

describe("text from the code host is text", () => {
	const hostile = "<script>alert(1)</script><img src=x onerror=alert(2)>";

	test("HTML in every field is shown as text", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							title: `Title ${hostile}`,
							summary: `Summary ${hostile}`,
							decisions: [
								{
									chosen: `Chosen ${hostile}`,
									alternative: `Alternative ${hostile}`,
									why: `Why ${hostile}`,
								},
							],
							reviewer_view: [`Reviewer ${hostile}`],
						}),
						item({
							number: 2,
							title: "Stopped",
							kind: "stopped",
							reason: `Reason ${hostile}`,
						}),
					],
				}),
			),
		);

		const listed = await card(`Title ${hostile}`);
		for (const text of [
			`Summary ${hostile}`,
			`Chosen ${hostile}`,
			`Instead of Alternative ${hostile}`,
			`Because Why ${hostile}`,
			`Reviewer ${hostile}`,
		]) {
			expect(
				listed.getByText(
					(_, element) =>
						element?.textContent === text &&
						Array.from(element.children).every(
							(child) => child.textContent !== text,
						),
				),
			).toBeTruthy();
		}
		expect((await card("Stopped")).getByText(`Reason ${hostile}`)).toBeTruthy();
		expect(document.querySelector("main script, main img")).toBeNull();
	});

	test("backticks become code and double asterisks bold, and nothing else is formatted", async () => {
		openWith(
			answering(
				list({
					items: [
						item({
							title: "The vault",
							summary: "Adds `vault.rs` and **seals** it, _not_ [this](x).",
						}),
					],
				}),
			),
		);

		const summary = (await card("The vault")).getByText(
			(_, element) =>
				element?.tagName === "P" &&
				(element.textContent ?? "").startsWith("Adds "),
		);
		expect(summary.innerHTML).toBe(
			"Adds <code>vault.rs</code> and <strong>seals</strong> it, _not_ [this](x).",
		);
	});

	test("no source file in devflow/web inserts HTML", () => {
		// Written in two halves, so this file does not name it itself.
		const insert = ["dangerously", "SetInnerHTML"].join("");
		// Vitest runs in devflow/web, the folder the gate runs it from.
		const root = process.cwd();
		expect(
			JSON.parse(readFileSync(join(root, "package.json"), "utf8")).name,
		).toBe("devflow-web");
		const sources: string[] = [];
		const walk = (dir: string) => {
			for (const entry of readdirSync(dir, { withFileTypes: true })) {
				if (["node_modules", "dist"].includes(entry.name)) continue;
				const path = join(dir, entry.name);
				if (entry.isDirectory()) walk(path);
				else if (/\.(ts|tsx|js|jsx|mjs|html)$/.test(entry.name))
					sources.push(path);
			}
		};
		walk(root);

		expect(sources.length).toBeGreaterThan(10);
		expect(
			sources.filter((path) => readFileSync(path, "utf8").includes(insert)),
		).toEqual([]);
	});
});

describe("questions before starting", () => {
	const CARD_ID = "33333333-3333-3333-3333-333333333333";
	const READING = "44444444-4444-4444-4444-444444444444";

	function asked(): WaitingList["question_cards"][number] {
		return {
			id: CARD_ID,
			project_id: PROJECT,
			project_name: "Personal-platform",
			owner: "Adamniels",
			number: 9,
			title: "Add the habits list",
			web_url: "https://github.com/Adamniels/Personal-platform/issues/9",
			feature: { number: 8, title: "Habits" },
			reading_id: READING,
			questions: [
				{
					question: "Is the list sorted by name or by date?",
					recommendation: "By date, newest first",
					why: "The page beside it sorts so",
				},
				{
					question: "Does an empty list show a sentence?",
					recommendation: "A sentence",
					why: "Nothing on the page looks broken",
				},
				{
					question: "Is the count beside the heading?",
					recommendation: "Yes",
					why: "As every group does",
				},
			],
			read_at: minutesAgo(3),
		};
	}

	function openAnswering(answerCard: ActionsApi["answerCard"]) {
		window.history.pushState(null, "", "/devflow");
		render(
			<App
				loadStatus={() =>
					Promise.resolve({
						code_host: "fake",
						github: { state: "linked", login: "octo-owner" },
					})
				}
				projects={{
					loadProjects: () => Promise.resolve([]),
					loadRepositories: () => new Promise(() => {}),
					connect: () => Promise.resolve(),
				}}
				waiting={answering(
					list({
						count: 2,
						items: [item({ number: 304, kind: "waiting" })],
						question_cards: [asked()],
					}),
				)}
				actions={{
					merge: () => Promise.resolve(),
					sendBack: () => Promise.resolve(),
					resume: () => Promise.resolve(),
					runCardAgain: () => Promise.resolve(),
					dropCard: () => Promise.resolve(),
					runFeatureAgain: () => Promise.resolve(),
					mergeCard: () => Promise.resolve(),
					sendBackCard: () => Promise.resolve(),
					loadFeatureMerge: () => Promise.reject(new Error("not offered")),
					mergeFeature: () => Promise.resolve(),
					sendBackFeature: () => Promise.resolve(),
					answerCard,
				}}
				now={() => NOW}
			/>,
		);
	}

	test("a question card shows each question with its recommendation, Accept and another answer, and Send answers stays disabled until each is answered", async () => {
		const answerCard = vi.fn<ActionsApi["answerCard"]>(() => Promise.resolve());
		openAnswering(answerCard);

		const entry = await choose("Add the habits list");
		const where = within(entry).getByText("Adamniels/Personal-platform")
			.parentElement as HTMLElement;
		expect(where.textContent).toBe(
			"Adamniels/Personal-platform · card #9 · feature #8",
		);
		const questions = within(entry).getAllByRole("group");
		expect(questions).toHaveLength(3);
		const [first, second, third] = questions.map((question) =>
			within(question as HTMLElement),
		) as [
			ReturnType<typeof within>,
			ReturnType<typeof within>,
			ReturnType<typeof within>,
		];

		// Each numbered, open, with its recommendation and why, Accept and
		// Write another answer, and no box until that is pressed.
		expect(first.getByText("01")).toBeTruthy();
		expect(third.getByText("03")).toBeTruthy();
		expect(first.getByText("Open")).toBeTruthy();
		expect(first.getByText("By date, newest first")).toBeTruthy();
		expect(first.getByText("The page beside it sorts so")).toBeTruthy();
		expect(first.getByRole("button", { name: "Accept" })).toBeTruthy();
		expect(
			first.getByRole("button", { name: "Write another answer" }),
		).toBeTruthy();
		expect(first.queryByRole("textbox")).toBeNull();

		// Send answers is the primary, disabled, with how many are left.
		const send = await within(entry).findByRole("button", {
			name: "Send answers",
		});
		expect(send.className).toMatch(/primary/);
		expect(send).toHaveProperty("disabled", true);
		expect(
			within(entry).getByText("3 questions left · sent as one comment"),
		).toBeTruthy();

		// Accepted, it says so, with Change, which opens it again.
		fireEvent.click(first.getByRole("button", { name: "Accept" }));
		expect(first.getByText("Accepted")).toBeTruthy();
		expect(first.queryByRole("button", { name: "Accept" })).toBeNull();
		fireEvent.click(first.getByRole("button", { name: "Change" }));
		expect(first.getByText("Open")).toBeTruthy();
		fireEvent.click(first.getByRole("button", { name: "Accept" }));

		// Another answer reveals the box; once written, it is Your answer.
		fireEvent.click(
			second.getByRole("button", { name: "Write another answer" }),
		);
		const box = second.getByLabelText("Another answer");
		expect(second.getByText("Open")).toBeTruthy();
		fireEvent.change(box, { target: { value: "No, nothing at all" } });
		expect(second.getByText("Your answer")).toBeTruthy();
		expect(
			within(entry).getByText("1 question left · sent as one comment"),
		).toBeTruthy();
		expect(send).toHaveProperty("disabled", true);

		// The last one answered, Send answers sends them in order.
		fireEvent.click(third.getByRole("button", { name: "Accept" }));
		expect(send).toHaveProperty("disabled", false);
		expect(
			within(entry).getByText("0 questions left · sent as one comment"),
		).toBeTruthy();
		fireEvent.click(send);
		await waitFor(() =>
			expect(answerCard).toHaveBeenCalledWith(CARD_ID, READING, [
				{ accept: true },
				{ text: "No, nothing at all" },
				{ accept: true },
			]),
		);
	});

	test("a card's questions are answered in place", async () => {
		const answerCard = vi
			.fn<ActionsApi["answerCard"]>()
			.mockRejectedValueOnce(
				new ApiError({
					status: 503,
					title: "GitHub did not answer",
					detail: "Nothing was sent. Try again in a moment.",
				}),
			)
			.mockResolvedValueOnce(undefined);
		openAnswering(answerCard);

		// The group comes first, one entry for the card, with its feature.
		const group = await panel().findByRole("region", {
			name: "Questions before starting",
		});
		expect(
			panel()
				.getAllByRole("region")
				.map((region) => region.getAttribute("aria-labelledby"))
				.map((id) => document.getElementById(id ?? "")?.textContent),
		).toEqual(["Questions before starting", "Pull requests waiting"]);
		expect(within(group).getAllByRole("link")).toHaveLength(1);
		const entry = within(await choose("Add the habits list"));
		expect(entry.getByText("Adamniels/Personal-platform")).toBeTruthy();
		expect(entry.getByText("card #9")).toBeTruthy();
		expect(entry.getByText("feature #8")).toBeTruthy();

		// Each question with the recommendation and why; nothing is sent
		// until every question has an answer.
		const questions = entry.getAllByRole("group");
		expect(questions).toHaveLength(3);
		const first = within(questions[0] as HTMLElement);
		const second = within(questions[1] as HTMLElement);
		const third = within(questions[2] as HTMLElement);
		expect(
			first.getByText("Is the list sorted by name or by date?"),
		).toBeTruthy();
		expect(first.getByText("By date, newest first")).toBeTruthy();
		expect(first.getByText("The page beside it sorts so")).toBeTruthy();
		const send = entry.getByRole("button", { name: "Send answers" });
		expect((send as HTMLButtonElement).disabled).toBe(true);

		// Accept the first and third, write another answer for the second.
		fireEvent.click(first.getByRole("button", { name: "Accept" }));
		expect(first.getByText("Accepted")).toBeTruthy();
		fireEvent.click(
			second.getByRole("button", { name: "Write another answer" }),
		);
		fireEvent.change(second.getByLabelText("Another answer"), {
			target: { value: "No, nothing at all" },
		});
		expect((send as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(third.getByRole("button", { name: "Accept" }));
		expect((send as HTMLButtonElement).disabled).toBe(false);

		// A failed send says why and keeps what was typed.
		fireEvent.click(send);
		expect((await entry.findByRole("alert")).textContent).toBe(
			"GitHub did not answer. Nothing was sent. Try again in a moment.",
		);
		expect(
			(second.getByLabelText("Another answer") as HTMLTextAreaElement).value,
		).toBe("No, nothing at all");
		expect(first.getByText("Accepted")).toBeTruthy();

		// Sent again, the answers go in the order of the questions.
		fireEvent.click(entry.getByRole("button", { name: "Send answers" }));
		await waitFor(() => expect(answerCard).toHaveBeenCalledTimes(2));
		expect(answerCard).toHaveBeenLastCalledWith(CARD_ID, READING, [
			{ accept: true },
			{ text: "No, nothing at all" },
			{ accept: true },
		]);
		expect(await screen.findByText("Answers sent; #9 is queued")).toBeTruthy();
	});
});

describe("the inbox", () => {
	const CARD = 21;

	function waitingCard(): WaitingList["cards"][number] {
		return {
			project_id: PROJECT,
			project_name: "Personal-platform",
			owner: "Adamniels",
			number: CARD,
			title: "Add the empty state",
			web_url: `https://github.com/Adamniels/Personal-platform/issues/${CARD}`,
			pull_request_number: 40,
			pull_request_url:
				"https://github.com/Adamniels/Personal-platform/pull/40",
			head_commit: "5".repeat(40),
			base: "main",
			summary: "Added the empty state.",
			decisions: [
				{ chosen: "A plain sentence", alternative: null, why: null },
				{ chosen: "On every list", alternative: null, why: null },
			],
			other_decisions: 0,
			review: null,
			check: { command: "npm test", exit_code: 0, commit: "5".repeat(40) },
			can_merge: true,
			changed_at: minutesAgo(2),
			screenshots: null,
			reason: null,
		};
	}

	function refused(): WaitingList["refused_cards"][number] {
		return {
			project_id: PROJECT,
			project_name: "Personal-platform",
			owner: "Adamniels",
			number: 4,
			title: "Count the habits",
			web_url: "https://github.com/Adamniels/Personal-platform/issues/4",
			missing: ["Which page shows the count"],
			refused_at: minutesAgo(5),
		};
	}

	function everything(): WaitingList {
		return list({
			count: 5,
			cards: [waitingCard()],
			refused_cards: [refused()],
			items: [
				item({ number: 304, title: "The vault" }),
				item({ number: 306, title: "Import a photo", kind: "stopped" }),
				item({ number: 307, title: "The board", sent_back: true }),
			],
		});
	}

	function queue() {
		return within(screen.getByRole("navigation", { name: "The queue" }));
	}

	test("the queue lists every group with its count and the first item is open", async () => {
		openWith(answering(everything()));

		await panel().findByRole("navigation", { name: "The queue" });
		// Every group the list holds, in its order, each with its count.
		expect(
			queue()
				.getAllByRole("region")
				.map((region) => region.getAttribute("aria-labelledby"))
				.map((id) => document.getElementById(id ?? "")?.textContent),
		).toEqual([
			"Pull requests waiting",
			"Stopped",
			"Refused",
			"Sent back, the runner has it",
		]);
		const waiting = within(
			queue().getByRole("region", { name: "Pull requests waiting" }),
		);
		expect(waiting.getByText("2")).toBeTruthy();
		// Each item a row with its title and a meta line of its project, its
		// number and the one fact that matters.
		expect(rows(screen.getByRole("navigation", { name: "The queue" }))).toEqual(
			[
				"Add the empty state Personal-platform · #40 · 2 decisions",
				"The vault Personal-platform · #304",
				"Import a photo Personal-platform · #306 · stopped",
				"Count the habits Personal-platform · card #4 · too thin",
				"The board Personal-platform · #307 · sent back",
			],
		);
		const meta = queue().getByText("Personal-platform · #304");
		expect(meta.className).toMatch(/meta/);
		// The queue has no button; the first item's detail is beside it.
		expect(queue().queryAllByRole("button")).toHaveLength(0);
		expect(panel().getAllByRole("article")).toHaveLength(1);
		expect(await card("Add the empty state")).toBeTruthy();
		expect(
			queue()
				.getByRole("link", { name: /Add the empty state/ })
				.getAttribute("aria-current"),
		).toBe("true");
	});

	test("choosing an item opens it and sets its address", async () => {
		openWith(answering(everything()));
		const push = vi.spyOn(window.history, "pushState");

		const vault = await panel().findByRole("link", { name: /The vault/ });
		fireEvent.click(vault);

		expect(await card("The vault")).toBeTruthy();
		expect(
			panel().queryByRole("article", { name: "Add the empty state" }),
		).toBeNull();
		expect(vault.getAttribute("aria-current")).toBe("true");
		expect(
			queue()
				.getByRole("link", { name: /Add the empty state/ })
				.getAttribute("aria-current"),
		).toBeNull();
		const key = item({ number: 304 }).id;
		expect(push).toHaveBeenCalledWith(null, "", `/devflow?item=${key}`);
		expect(window.location.search).toBe(`?item=${key}`);
		expect(vault.getAttribute("href")).toBe(`/devflow?item=${key}`);
		// Each kind has its own key.
		expect(
			queue()
				.getByRole("link", { name: /Add the empty state/ })
				.getAttribute("href"),
		).toBe(`/devflow?item=card/${PROJECT}/${CARD}`);
		expect(
			queue()
				.getByRole("link", { name: /Count the habits/ })
				.getAttribute("href"),
		).toBe(`/devflow?item=refused/${PROJECT}/4`);
		push.mockRestore();
	});

	test("an item's screen has the way back and its place in the queue", async () => {
		openWith(answering(everything()), `/devflow?item=refused/${PROJECT}/4`);
		expect(await card("Count the habits")).toBeTruthy();

		// The inbox is open on the item the address names, the fourth of five.
		const inbox = screen.getByRole("navigation", { name: "The queue" })
			.parentElement as HTMLElement;
		expect(inbox.hasAttribute("data-open")).toBe(true);
		expect(panel().getByText("4 of 5")).toBeTruthy();
		const back = panel().getByRole("link", { name: "Waiting for you" });
		expect(back.getAttribute("href")).toBe("/devflow");

		fireEvent.click(back);
		expect(window.location.search).toBe("");
		expect(inbox.hasAttribute("data-open")).toBe(false);
		expect(panel().getByText("1 of 5")).toBeTruthy();
	});

	test("an address opens its item, an unknown one the first", async () => {
		openWith(answering(everything()), `/devflow?item=refused/${PROJECT}/4`);
		expect(await card("Count the habits")).toBeTruthy();
		expect(panel().getAllByRole("article")).toHaveLength(1);

		// Back and Forward come back as popstate, and the item follows.
		window.history.pushState(null, "", `/devflow?item=card/${PROJECT}/${CARD}`);
		window.dispatchEvent(new PopStateEvent("popstate"));
		expect(await card("Add the empty state")).toBeTruthy();
		cleanup();

		openWith(answering(everything()), "/devflow?item=card/nobody/1");
		expect(await card("Add the empty state")).toBeTruthy();
		expect(panel().getAllByRole("article")).toHaveLength(1);
	});
});
