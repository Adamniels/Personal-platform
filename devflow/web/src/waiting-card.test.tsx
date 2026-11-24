import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import type { ActionsApi } from "./actions";
import { ApiError } from "./api/client";
import { App } from "./app";
import type { Status } from "./status";
import type { CardItem, WaitingApi, WaitingItem, WaitingList } from "./waiting";

const NOW = Date.parse("2026-11-18T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const HEAD = `abc1234${"0".repeat(33)}`;
const TITLE = "Add the empty state";

function card(overrides: Partial<CardItem> = {}): CardItem {
	return {
		project_id: PROJECT,
		project_name: "Fodr",
		owner: "Adamniels",
		number: 4,
		title: TITLE,
		web_url: "https://github.com/Adamniels/Fodr/issues/4",
		pull_request_number: 7,
		pull_request_url: "https://github.com/Adamniels/Fodr/pull/7",
		head_commit: HEAD,
		base: "main",
		summary: "Added the `empty state` to the habits list.",
		decisions: [
			{
				chosen: "Show it on every list",
				alternative: "Only on habits",
				why: "The lists look the same",
			},
		],
		other_decisions: 2,
		review: null,
		check: { command: "npm ci && npm test", exit_code: 0, commit: HEAD },
		can_merge: true,
		changed_at: "2026-11-18T11:55:00+00:00",
		screenshots: null,
		reason: "Waiting for CI to report on its commit",
		...overrides,
	};
}

function list(cards: CardItem[], items: WaitingItem[] = []): WaitingList {
	return {
		count: cards.length + items.length,
		needs_relink: false,
		stopped_cards: [],
		refused_cards: [],
		question_cards: [],
		failing_projects: [],
		setup_projects: [],
		setup_runs: [],
		features: [],
		items,
		projects: [
			{
				id: PROJECT,
				owner: "Adamniels",
				name: "Fodr",
				last_success_at: new Date(NOW - 60_000).toISOString(),
				stale: false,
				failing: false,
			},
		],
		own_items: [],
		cards,
		decisions: [],
	};
}

function status(state: Status["github"]["state"] = "linked"): Status {
	return {
		code_host: "fake",
		github: { state, login: state === "none" ? null : "octo-owner" },
	};
}

/** The page on the waiting tab with the cards, its list read again after a
 * merge answering `after`. */
function open(
	cards: CardItem[],
	{
		mergeCard = () => Promise.resolve(),
		sendBackCard = () => Promise.resolve(),
		after = cards,
		link = "linked",
		items = [],
	}: {
		mergeCard?: ActionsApi["mergeCard"];
		sendBackCard?: ActionsApi["sendBackCard"];
		after?: CardItem[];
		link?: Status["github"]["state"];
		items?: WaitingItem[];
	} = {},
) {
	window.history.pushState(null, "", "/devflow");
	let answer = list(cards, items);
	const loads = vi.fn(() => Promise.resolve(answer));
	const waiting: WaitingApi = {
		loadWaiting: loads,
		refresh: () => Promise.resolve(),
	};
	const merges = vi.fn(
		async (project: string, number: number, commit: string) => {
			await mergeCard(project, number, commit);
			answer = list(after, items);
		},
	);
	const sends = vi.fn(
		async (project: string, number: number, comment: string) => {
			await sendBackCard(project, number, comment);
			answer = list(after, items);
		},
	);
	render(
		<App
			loadStatus={() => Promise.resolve(status(link))}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
			}}
			waiting={waiting}
			actions={{
				merge: () => Promise.resolve(),
				sendBack: () => Promise.resolve(),
				resume: () => Promise.resolve(),
				mergeCard: merges,
				sendBackCard: sends,
				loadFeatureMerge: () => Promise.reject(new Error("not offered")),
				mergeFeature: () => Promise.resolve(),
				sendBackFeature: () => Promise.resolve(),
				answerCard: () => Promise.resolve(),
			}}
			now={() => NOW}
		/>,
	);
	return { merges, sends, loads };
}

async function item() {
	return within(
		await within(screen.getByRole("tabpanel")).findByRole("article", {
			name: TITLE,
		}),
	);
}

// jsdom has HTMLDialogElement but neither showModal nor close, as in
// actions.test.tsx.
beforeEach(() => {
	HTMLDialogElement.prototype.showModal = function showModal() {
		this.open = true;
	};
	HTMLDialogElement.prototype.close = function close() {
		this.open = false;
	};
});

afterEach(cleanup);

describe("a card of Dev flow's that waits", () => {
	test("is under Pull requests waiting, counted, with what to judge it by", async () => {
		open([card()]);

		const shown = await item();
		const region = screen.getByRole("region", {
			name: /Pull requests waiting/,
		});
		expect(within(region).getByRole("article", { name: TITLE })).toBeTruthy();
		expect(screen.getByRole("tab", { name: "Waiting for you 1" })).toBeTruthy();
		expect(shown.getByText("Waiting for you", { exact: true })).toBeTruthy();
		expect(shown.getByText("Adamniels/Fodr")).toBeTruthy();
		expect(shown.getByText("#7")).toBeTruthy();
		expect(shown.getByText("empty state").tagName).toBe("CODE");
		// The decision for the owner, with its alternative and why, and the
		// others counted.
		const decided = within(
			shown.getByRole("list", { name: "The agent decided" }),
		);
		expect(decided.getByText("Show it on every list")).toBeTruthy();
		expect(decided.getByText("Only on habits")).toBeTruthy();
		expect(decided.getByText("The lists look the same")).toBeTruthy();
		expect(shown.getByText("and 2 more it did not need you for")).toBeTruthy();
		// The check Dev flow ran.
		expect(shown.getByText("npm ci && npm test").tagName).toBe("CODE");
		expect(shown.getByText("abc1234").tagName).toBe("CODE");
		expect(shown.getByText(/exit 0\./)).toBeTruthy();
		// Merge, the view's one primary, and the pull request on GitHub.
		const merge = shown.getByRole("button", { name: "Merge" });
		expect(merge.className).toMatch(/primary/);
		expect(merge).toHaveProperty("disabled", false);
		const link = shown.getByRole("link", { name: "Open on GitHub" });
		expect(link.getAttribute("href")).toBe(
			"https://github.com/Adamniels/Fodr/pull/7",
		);
		expect(link.getAttribute("rel")).toBe("noreferrer");
		// Send back, beside it and not primary, which starts the card's fix.
		const back = shown.getByRole("button", { name: "Send back" });
		expect(back.className).not.toMatch(/primary/);
		expect(back).toHaveProperty("disabled", false);
	});

	test("says why it waits, in words", async () => {
		open([
			card({ reason: "This project waits for you on every pull request" }),
		]);

		const shown = await item();
		expect(shown.getByRole("heading", { name: "Why it waits" })).toBeTruthy();
		expect(
			shown.getByText("This project waits for you on every pull request"),
		).toBeTruthy();
	});

	test("without a reason, there is no reason block", async () => {
		open([card({ reason: null })]);

		const shown = await item();
		expect(shown.queryByRole("heading", { name: "Why it waits" })).toBeNull();
	});

	test("shows the reviewer's verdict on its commit", async () => {
		open([
			card({
				review: {
					verdict: "changes",
					summary: "The empty state shows only after a `reload`.",
					blocking: 2,
					commit: HEAD,
				},
			}),
		]);

		const shown = await item();
		expect(shown.getByRole("heading", { name: "The review" })).toBeTruthy();
		expect(shown.getByText("Changes asked for")).toBeTruthy();
		expect(shown.getByText(/, 2 findings block it\./)).toBeTruthy();
		expect(shown.getByText("reload").tagName).toBe("CODE");
	});

	test("shows the pictures of its commit, read from the card's route", async () => {
		open([
			card({
				screenshots: {
					commit: HEAD,
					groups: [{ name: "home", images: ["home-desktop", "home-phone"] }],
					note: null,
				},
			}),
		]);

		const shown = await item();
		const shots = within(shown.getByRole("list", { name: "Screenshots" }));
		expect(shots.getByText("home")).toBeTruthy();
		expect(
			shots.getAllByRole("img").map((image) => image.getAttribute("src")),
		).toEqual([
			`/devflow/api/projects/${PROJECT}/cards/4/screenshots/home-desktop`,
			`/devflow/api/projects/${PROJECT}/cards/4/screenshots/home-phone`,
		]);
	});

	test("an approve with nothing blocking says so, and no review says nothing", async () => {
		open([
			card({
				review: { verdict: "approve", summary: "", blocking: 0, commit: HEAD },
			}),
			card({ number: 5, title: "Count the habits", pull_request_number: 8 }),
		]);

		const approved = await item();
		expect(approved.getByText("Approved")).toBeTruthy();
		expect(approved.queryByText(/block/)).toBeNull();
		const unreviewed = within(
			screen.getByRole("article", { name: "Count the habits" }),
		);
		expect(
			unreviewed.queryByRole("heading", { name: "The review" }),
		).toBeNull();
	});

	test("shows what the agent wrote as text", async () => {
		open([
			card({
				summary: '<img src=x onerror="alert(1)"> **done**',
				decisions: [
					{ chosen: "<script>x</script>", alternative: null, why: null },
				],
			}),
		]);

		const shown = await item();
		expect(shown.getByText(/<img src=x onerror="alert\(1\)">/)).toBeTruthy();
		expect(shown.getByText("done").tagName).toBe("STRONG");
		expect(shown.getByText("<script>x</script>")).toBeTruthy();
		expect(document.querySelector("img, script")).toBeNull();
	});

	test("a pull request address that is not https is not linked", async () => {
		open([card({ pull_request_url: "javascript:alert(1)" })]);

		expect((await item()).queryByRole("link")).toBeNull();
	});

	test("is not merged while its project is stopped, or without a link", async () => {
		open([card({ can_merge: false })]);
		const stopped = await item();
		expect(stopped.getByRole("button", { name: "Merge" })).toHaveProperty(
			"disabled",
			true,
		);
		expect(
			stopped.getByText("The project is stopped: resume it before you merge."),
		).toBeTruthy();
		cleanup();

		open([card()], { link: "needs_relink" });
		const expired = await item();
		await waitFor(() =>
			expect(expired.getByRole("button", { name: "Merge" })).toHaveProperty(
				"disabled",
				true,
			),
		);
		expect(expired.getByText(/Your GitHub link has expired/)).toBeTruthy();
	});

	test("merging asks first, naming the pull request, its base and the commit", async () => {
		const { merges } = open([card()]);

		fireEvent.click((await item()).getByRole("button", { name: "Merge" }));
		const dialog = within(
			await screen.findByRole("dialog", { name: "Merge #7 into main?" }),
		);
		expect(dialog.getByText(TITLE)).toBeTruthy();
		expect(dialog.getByText("abc1234").tagName).toBe("CODE");
		expect(
			dialog.getByText("This confirms 1 decision the agent made."),
		).toBeTruthy();
		fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(merges).not.toHaveBeenCalled();
	});

	test("merging sends the card and the commit shown, and it leaves", async () => {
		const { merges, loads } = open([card()], { after: [] });

		fireEvent.click((await item()).getByRole("button", { name: "Merge" }));
		fireEvent.click(
			within(await screen.findByRole("dialog")).getByRole("button", {
				name: "Merge",
			}),
		);

		await waitFor(() => expect(merges).toHaveBeenCalledWith(PROJECT, 4, HEAD));
		await waitFor(() =>
			expect(
				within(screen.getByRole("tabpanel")).queryByRole("article", {
					name: TITLE,
				}),
			).toBeNull(),
		);
		expect(loads.mock.calls.length).toBeGreaterThan(1);
		expect(await screen.findByText("Merged #7")).toBeTruthy();
	});

	test("a refused merge is said in the item, and it stays", async () => {
		open([card()], {
			mergeCard: () =>
				Promise.reject(
					new ApiError({
						status: 409,
						title: "The pull request has changed since this was shown",
						detail: "It was read again. Look at it before you merge.",
					}),
				),
		});

		fireEvent.click((await item()).getByRole("button", { name: "Merge" }));
		fireEvent.click(
			within(await screen.findByRole("dialog")).getByRole("button", {
				name: "Merge",
			}),
		);

		expect(await (await item()).findByRole("alert")).toHaveProperty(
			"textContent",
			"The pull request has changed since this was shown. It was read again. Look at it before you merge.",
		);
	});

	test("sending back asks for a comment, sends it with the card, and it leaves", async () => {
		const { sends, loads } = open([card()], { after: [] });

		fireEvent.click((await item()).getByRole("button", { name: "Send back" }));
		const box = (await item()).getByRole("textbox", {
			name: "What should the developer change?",
		});
		const send = (await item()).getByRole("button", { name: "Send" });
		expect(send).toHaveProperty("disabled", true);
		fireEvent.change(box, { target: { value: "Name it EmptyList." } });
		fireEvent.click(send);

		await waitFor(() =>
			expect(sends).toHaveBeenCalledWith(PROJECT, 4, "Name it EmptyList."),
		);
		await waitFor(() =>
			expect(
				within(screen.getByRole("tabpanel")).queryByRole("article", {
					name: TITLE,
				}),
			).toBeNull(),
		);
		expect(loads.mock.calls.length).toBeGreaterThan(1);
		expect(await screen.findByText("Sent back #7")).toBeTruthy();
	});

	test("sending back can be cancelled, and needs a link", async () => {
		open([card()], { link: "none" });

		expect(
			(await item()).getByRole("button", { name: "Send back" }),
		).toHaveProperty("disabled", true);
		cleanup();

		const { sends } = open([card()]);
		fireEvent.click((await item()).getByRole("button", { name: "Send back" }));
		fireEvent.click((await item()).getByRole("button", { name: "Cancel" }));
		expect((await item()).queryByRole("textbox")).toBeNull();
		expect(sends).not.toHaveBeenCalled();
	});

	test("a refused send back is said in the item, and it stays", async () => {
		open([card()], {
			sendBackCard: () =>
				Promise.reject(
					new ApiError({
						status: 409,
						title: "This card cannot be sent back now",
						detail:
							"A card is sent back while it waits for you or has stopped, with its pull request open.",
					}),
				),
		});

		fireEvent.click((await item()).getByRole("button", { name: "Send back" }));
		fireEvent.change(
			(await item()).getByRole("textbox", {
				name: "What should the developer change?",
			}),
			{ target: { value: "Rename it" } },
		);
		fireEvent.click((await item()).getByRole("button", { name: "Send" }));

		expect(await (await item()).findByRole("alert")).toHaveProperty(
			"textContent",
			"This card cannot be sent back now. A card is sent back while it waits for you or has stopped, with its pull request open.",
		);
	});
});
