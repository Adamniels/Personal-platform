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
import { App } from "./app";
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
		read_at: minutesAgo(1),
		...overrides,
	};
}

function list(overrides: Partial<WaitingList> = {}): WaitingList {
	const items = overrides.items ?? [];
	return {
		count: items.filter((each) => !each.sent_back).length,
		needs_relink: false,
		items,
		projects: [project()],
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
				disconnect: () => Promise.resolve(),
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

/** The item's card, by its title. */
async function card(title: string) {
	return within(await panel().findByRole("article", { name: title }));
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
		expect(within(waiting).getAllByRole("article")).toHaveLength(2);
		expect(within(stopped).getAllByRole("article")).toHaveLength(1);
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
		expect(
			within(waiting)
				.getAllByRole("article")
				.map((article) => article.getAttribute("aria-labelledby"))
				.map((id) => document.getElementById(id ?? "")?.textContent),
		).toEqual(["Fodr forty", "Platform three", "Platform twelve"]);
	});

	test("an item's first line holds the project, its number, its title and its status", async () => {
		openWith(
			answering(list({ items: [item({ number: 304, title: "The vault" })] })),
		);

		const vault = await card("The vault");
		expect(vault.getByText("Adamniels/Personal-platform")).toBeTruthy();
		expect(vault.getByText("#304")).toBeTruthy();
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
		expect((await card("Stops")).getByText("Stopped")).toBeTruthy();
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
		// The decisions answer why it waits, so the reason is not repeated.
		expect(vault.queryByText("Why it waits")).toBeNull();
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
		expect(vault.queryAllByRole("button")).toHaveLength(0);
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
		expect(vault.getByText("Why it waits")).toBeTruthy();
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
		expect(stopped.queryByText("Why it waits")).toBeNull();
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
		expect(
			within(sentBack)
				.getAllByRole("article")
				.map((article) => article.getAttribute("aria-labelledby"))
				.map((id) => document.getElementById(id ?? "")?.textContent),
		).toEqual(["Went back", "Stopped and went back"]);
		const waiting = panel().getByRole("region", {
			name: "Pull requests waiting",
		});
		expect(within(waiting).getAllByRole("article")).toHaveLength(1);
		expect(within(waiting).getByText("1")).toBeTruthy();
		expect(panel().queryByRole("region", { name: "Stopped" })).toBeNull();
		const wentBack = await card("Went back");
		expect(wentBack.getByText("Sent back")).toBeTruthy();
		expect(wentBack.queryByText("Needs you")).toBeNull();
		await waitFor(() =>
			expect(
				screen.getByRole("tab", { name: /Waiting for you/ }).textContent,
			).toBe("Waiting for you 1"),
		);
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
