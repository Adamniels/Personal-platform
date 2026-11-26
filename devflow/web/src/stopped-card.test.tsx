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
import { UNLINKED } from "./actions";
import { ApiError } from "./api/client";
import { App } from "./app";
import type { Status } from "./status";
import { RUN_AGAIN } from "./stopped-card";
import type {
	FailingProjectItem,
	StoppedCardItem,
	StoppedFeatureItem,
	WaitingApi,
	WaitingList,
} from "./waiting";

const NOW = Date.parse("2027-01-15T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const REASON =
	"Failed twice in a row with no new commit: The run failed: it reached the most turns, 200 turns";

function stopped(overrides: Partial<StoppedCardItem> = {}): StoppedCardItem {
	return {
		project_id: PROJECT,
		project_name: "Fodr",
		owner: "Adamniels",
		number: 12,
		title: "Show the empty state",
		web_url: "https://github.com/Adamniels/Fodr/issues/12",
		reason: REASON,
		stopped_at: "2027-01-15T11:50:00+00:00",
		pull_request_url: null,
		stopped_by_owner: false,
		feature_number: null,
		...overrides,
	};
}

/** A card the owner stopped himself, on its own. */
function mine(overrides: Partial<StoppedCardItem> = {}): StoppedCardItem {
	return stopped({
		stopped_by_owner: true,
		reason: "Stopped by you",
		...overrides,
	});
}

function held(overrides: Partial<StoppedFeatureItem> = {}): StoppedFeatureItem {
	return {
		project_id: PROJECT,
		project_name: "Fodr",
		owner: "Adamniels",
		number: 38,
		title: "Feeding plan, version 2",
		web_url: "https://github.com/Adamniels/Fodr/issues/38",
		state: "reviewing",
		reason: "Stopped by you",
		stopped_at: "2027-01-15T11:40:00+00:00",
		...overrides,
	};
}

function failing(
	overrides: Partial<FailingProjectItem> = {},
): FailingProjectItem {
	return {
		project_id: PROJECT,
		project_name: "Fodr",
		owner: "Adamniels",
		message:
			"Dev flow cannot run Fodr's cards: Claude Code is not logged in with the token",
		reason: "Claude Code is not logged in with the token",
		failures: 6,
		since: "2027-01-15T09:00:00+00:00",
		wait_until: "2027-01-15T12:30:00+00:00",
		...overrides,
	};
}

function list(
	stoppedCards: StoppedCardItem[],
	failingProjects: FailingProjectItem[] = [],
	stoppedFeatures: StoppedFeatureItem[] = [],
): WaitingList {
	return {
		count:
			stoppedCards.length + failingProjects.length + stoppedFeatures.length,
		needs_relink: false,
		features: [],
		items: [],
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
		cards: [],
		stopped_cards: stoppedCards,
		stopped_features: stoppedFeatures,
		refused_cards: [],
		question_cards: [],
		failing_projects: failingProjects,
		setup_projects: [],
		setup_runs: [],
		decisions: [],
	};
}

function status(login: Status["github"]["state"] = "linked"): Status {
	return {
		code_host: "fake",
		github: { state: login, login: login === "none" ? null : "octo-owner" },
	};
}

/** The page on the waiting tab with the list, read again after a comment
 * answering `after`. */
function open(
	answer: WaitingList,
	{
		after = answer,
		login = "linked",
	}: { after?: WaitingList; login?: Status["github"]["state"] } = {},
) {
	window.history.pushState(null, "", "/devflow");
	let current = answer;
	const loadWaiting = vi.fn(() => Promise.resolve(current));
	const waiting: WaitingApi = {
		loadWaiting,
		refresh: () => Promise.resolve(),
	};
	const sendBackCard = vi.fn<ActionsApi["sendBackCard"]>(async () => {
		current = after;
	});
	const merge = vi.fn<ActionsApi["merge"]>(() => Promise.resolve());
	const runCardAgain = vi.fn<ActionsApi["runCardAgain"]>(async () => {
		current = after;
	});
	const dropCard = vi.fn<ActionsApi["dropCard"]>(async () => {
		current = after;
	});
	const runFeatureAgain = vi.fn<ActionsApi["runFeatureAgain"]>(async () => {
		current = after;
	});
	render(
		<App
			loadStatus={() => Promise.resolve(status(login))}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
			}}
			waiting={waiting}
			actions={{
				merge,
				sendBack: () => Promise.resolve(),
				resume: () => Promise.resolve(),
				runCardAgain,
				dropCard,
				runFeatureAgain,
				mergeCard: () => Promise.resolve(),
				sendBackCard,
				loadFeatureMerge: () => Promise.reject(new Error("not offered")),
				mergeFeature: () => Promise.resolve(),
				sendBackFeature: () => Promise.resolve(),
				answerCard: () => Promise.resolve(),
			}}
			now={() => NOW}
		/>,
	);
	return {
		sendBackCard,
		merge,
		runCardAgain,
		dropCard,
		runFeatureAgain,
		loadWaiting,
	};
}

/** The buttons drawn as the primary in what is shown. */
function primaries(within_: ReturnType<typeof within>) {
	return within_
		.getAllByRole("button")
		.filter((button: HTMLElement) => /primary/.test(button.className));
}

/** The tab, once the Stopped group is in its queue; its first item is
 * open. */
async function stoppedTab() {
	const tab = within(screen.getByRole("tabpanel"));
	await tab.findByRole("region", { name: "Stopped" });
	return tab;
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

describe("a stopped card", () => {
	test("shows under Stopped with why, and offers a comment and its issue, never Merge", async () => {
		open(list([stopped()]));

		const group = await stoppedTab();
		const card = within(
			group.getByRole("article", { name: "Show the empty state" }),
		);
		expect(card.getByText("Stopped")).toBeTruthy();
		expect(card.getByText("Adamniels/Fodr")).toBeTruthy();
		expect(card.getByText("card #12")).toBeTruthy();
		expect(card.getByText("Why it stopped")).toBeTruthy();
		expect(card.getByText(REASON)).toBeTruthy();
		expect(card.getByRole("button", { name: RUN_AGAIN })).toBeTruthy();
		expect(
			card.getByRole("link", { name: "Open on GitHub" }).getAttribute("href"),
		).toBe("https://github.com/Adamniels/Fodr/issues/12");
		expect(card.queryByRole("button", { name: "Merge" })).toBeNull();
	});

	test("a stopped card shows why on a danger inset and Comment to run again as the primary", async () => {
		open(list([stopped()]));

		const group = await stoppedTab();
		const card = group.getByRole("article", { name: "Show the empty state" });
		// Why it stopped, its label and the reason, on one danger inset.
		const why = within(card).getByText(REASON).parentElement as HTMLElement;
		expect(why.className).toMatch(/danger/);
		expect(within(why).getByText("Why it stopped")).toBeTruthy();
		// The comment is the one primary; pressing it opens the box and its
		// Send, as today, and Send takes its place as the primary.
		const comment = within(card).getByRole("button", { name: RUN_AGAIN });
		await waitFor(() => expect(comment).toHaveProperty("disabled", false));
		expect(comment.className).toMatch(/primary/);
		expect(
			within(card)
				.getAllByRole("button")
				.filter((button) => /primary/.test(button.className)),
		).toHaveLength(1);
		fireEvent.click(comment);
		expect(
			within(card).getByLabelText("A comment runs it again."),
		).toBeTruthy();
		expect(within(card).queryByRole("button", { name: RUN_AGAIN })).toBeNull();
		const send = within(card).getByRole("button", { name: "Send" });
		expect(send.className).toMatch(/primary/);
		expect(
			within(card)
				.getAllByRole("button")
				.filter((button) => /primary/.test(button.className)),
		).toHaveLength(1);
	});

	test("Comment to run again posts the owner's comment on the card, and the list is read again", async () => {
		const { sendBackCard } = open(list([stopped()]), { after: list([]) });

		const group = await stoppedTab();
		fireEvent.click(group.getByRole("button", { name: RUN_AGAIN }));
		fireEvent.change(group.getByLabelText("A comment runs it again."), {
			target: { value: "Leave the sorting out." },
		});
		fireEvent.click(group.getByRole("button", { name: "Send" }));

		await waitFor(() =>
			expect(sendBackCard).toHaveBeenCalledWith(
				PROJECT,
				12,
				"Leave the sorting out.",
			),
		);
		expect(
			await screen.findByText("Commented on #12; it runs again"),
		).toBeTruthy();
		await waitFor(() =>
			expect(screen.queryByRole("region", { name: "Stopped" })).toBeNull(),
		);
	});

	test("without a link, it says how to comment and offers nothing to send", async () => {
		open(list([stopped()]), { login: "none" });

		const group = await stoppedTab();
		await waitFor(() =>
			expect(
				(
					group.getByRole("button", {
						name: RUN_AGAIN,
					}) as HTMLButtonElement
				).disabled,
			).toBe(true),
		);
		expect(group.getByText(UNLINKED)).toBeTruthy();
	});

	test("an address that is not https is shown as no link", async () => {
		open(list([stopped({ web_url: "javascript:alert(1)" })]));

		const group = await stoppedTab();
		expect(group.queryByRole("link", { name: "Open on GitHub" })).toBeNull();
	});
});

describe("a card or a feature stopped by me", () => {
	test("a card stopped by me offers Run again and Drop, not a comment", async () => {
		const { dropCard, sendBackCard, runCardAgain, loadWaiting } = open(
			list([mine()]),
			{ after: list([]) },
		);

		const group = await stoppedTab();
		const card = within(
			group.getByRole("article", { name: "Show the empty state" }),
		);
		expect(card.getByText("Stopped by you")).toBeTruthy();
		expect(card.queryByRole("button", { name: RUN_AGAIN })).toBeNull();
		expect(card.queryByLabelText("A comment runs it again.")).toBeNull();
		expect(card.queryByText("Why it stopped")).toBeNull();
		expect(card.queryByRole("button", { name: "Merge" })).toBeNull();
		const run = card.getByRole("button", { name: "Run again" });
		expect(primaries(card)).toEqual([run]);
		expect(
			card.getByRole("link", { name: "Open on GitHub" }).getAttribute("href"),
		).toBe("https://github.com/Adamniels/Fodr/issues/12");

		// Drop asks first, and sends nothing until it is confirmed.
		const drop = card.getByRole("button", { name: "Drop" });
		await waitFor(() => expect(drop).toHaveProperty("disabled", false));
		fireEvent.click(drop);
		const dialog = within(screen.getByRole("dialog", { name: "Drop #12?" }));
		expect(
			dialog.getByText("Dev flow takes its label off and leaves its branch."),
		).toBeTruthy();
		expect(dropCard).not.toHaveBeenCalled();
		const reads = loadWaiting.mock.calls.length;
		fireEvent.click(dialog.getByRole("button", { name: "Drop" }));

		await waitFor(() => expect(dropCard).toHaveBeenCalledWith(PROJECT, 12));
		expect(await screen.findByText("Dropped #12")).toBeTruthy();
		await waitFor(() =>
			expect(loadWaiting.mock.calls.length).toBeGreaterThan(reads),
		);
		expect(runCardAgain).not.toHaveBeenCalled();
		expect(sendBackCard).not.toHaveBeenCalled();
	});

	test("Run again on a card I stopped runs it, says so, and reads the list again", async () => {
		const { runCardAgain, loadWaiting } = open(list([mine()]), {
			after: list([]),
		});

		const group = await stoppedTab();
		const reads = loadWaiting.mock.calls.length;
		fireEvent.click(group.getByRole("button", { name: "Run again" }));

		await waitFor(() => expect(runCardAgain).toHaveBeenCalledWith(PROJECT, 12));
		expect(await screen.findByText("Running #12 again")).toBeTruthy();
		await waitFor(() =>
			expect(loadWaiting.mock.calls.length).toBeGreaterThan(reads),
		);
		expect(await screen.findByText("Nothing waits for you")).toBeTruthy();
	});

	test("a card of a feature I stopped offers Run again and no Drop", async () => {
		open(list([mine({ feature_number: 38 })]));

		const group = await stoppedTab();
		const card = within(
			group.getByRole("article", { name: "Show the empty state" }),
		);
		expect(card.getByRole("button", { name: "Run again" })).toBeTruthy();
		expect(card.queryByRole("button", { name: "Drop" })).toBeNull();
	});

	test("without a usable link, Drop is disabled and says why, and Run again is not", async () => {
		open(list([mine()]), { login: "none" });

		const group = await stoppedTab();
		const card = within(
			group.getByRole("article", { name: "Show the empty state" }),
		);
		await waitFor(() => expect(card.getByText(UNLINKED)).toBeTruthy());
		expect(card.getByRole("button", { name: "Drop" })).toHaveProperty(
			"disabled",
			true,
		);
		expect(card.getByRole("button", { name: "Run again" })).toHaveProperty(
			"disabled",
			false,
		);
	});

	test("a refusal is said in the card, and it stays", async () => {
		const { runCardAgain } = open(list([mine()]));
		runCardAgain.mockRejectedValueOnce(
			new ApiError({
				status: 409,
				title: "This card was not stopped by you",
				detail:
					"Run again is for a card you stopped. A card Dev flow stopped runs again on your comment.",
			}),
		);

		const group = await stoppedTab();
		fireEvent.click(group.getByRole("button", { name: "Run again" }));

		expect((await group.findByRole("alert")).textContent).toContain(
			"This card was not stopped by you",
		);
		expect(
			group.getByRole("article", { name: "Show the empty state" }),
		).toBeTruthy();
	});

	test("a feature I stopped is listed with Run again, its page and its issue", async () => {
		const { runFeatureAgain } = open(list([], [], [held()]), {
			after: list([]),
		});

		const group = await stoppedTab();
		const feature = within(
			group.getByRole("article", { name: "Feeding plan, version 2" }),
		);
		expect(feature.getByText("Stopped by you")).toBeTruthy();
		expect(feature.getByText("feature #38")).toBeTruthy();
		expect(feature.getByText(/You stopped its review/)).toBeTruthy();
		expect(
			feature
				.getByRole("link", { name: /Open the feature/ })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${PROJECT}/features/38`);
		expect(feature.queryByRole("button", { name: "Drop" })).toBeNull();
		const run = feature.getByRole("button", { name: "Run again" });
		expect(primaries(feature)).toEqual([run]);

		fireEvent.click(run);
		await waitFor(() =>
			expect(runFeatureAgain).toHaveBeenCalledWith(PROJECT, 38),
		);
		expect(await screen.findByText("Running #38 again")).toBeTruthy();
	});
});

describe("a project that cannot run its cards", () => {
	test("shows under Stopped with its message, counted with the stopped cards", async () => {
		open(list([stopped()], [failing()]));

		const group = await stoppedTab();
		const project = within(
			group.getByRole("article", {
				name: "Dev flow cannot run Fodr's cards: Claude Code is not logged in with the token",
			}),
		);
		expect(project.getByText("Cannot run")).toBeTruthy();
		expect(
			project.getByText(/6 runs in a row failed outside their cards/),
		).toBeTruthy();
		const heading = screen.getByRole("heading", { name: /Stopped/ });
		expect(heading.textContent).toContain("2");
	});

	test("alone, the list is not empty", async () => {
		open(list([], [failing()]));

		expect(await stoppedTab()).toBeTruthy();
		expect(screen.queryByText("Nothing waits for you")).toBeNull();
	});
});
