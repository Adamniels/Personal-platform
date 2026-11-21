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
import { App } from "./app";
import type { Status } from "./status";
import { RUN_AGAIN } from "./stopped-card";
import type {
	FailingProjectItem,
	StoppedCardItem,
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
): WaitingList {
	return {
		count: stoppedCards.length + failingProjects.length,
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
		failing_projects: failingProjects,
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
	const waiting: WaitingApi = {
		loadWaiting: vi.fn(() => Promise.resolve(current)),
		refresh: () => Promise.resolve(),
	};
	const sendBackCard = vi.fn<ActionsApi["sendBackCard"]>(async () => {
		current = after;
	});
	const merge = vi.fn<ActionsApi["merge"]>(() => Promise.resolve());
	render(
		<App
			loadStatus={() => Promise.resolve(status(login))}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
				disconnect: () => Promise.resolve(),
			}}
			waiting={waiting}
			actions={{
				merge,
				sendBack: () => Promise.resolve(),
				resume: () => Promise.resolve(),
				mergeCard: () => Promise.resolve(),
				sendBackCard,
				loadFeatureMerge: () => Promise.reject(new Error("not offered")),
				mergeFeature: () => Promise.resolve(),
			}}
			now={() => NOW}
		/>,
	);
	return { sendBackCard, merge };
}

async function stoppedGroup() {
	return within(
		await within(screen.getByRole("tabpanel")).findByRole("region", {
			name: "Stopped",
		}),
	);
}

afterEach(cleanup);

describe("a stopped card", () => {
	test("shows under Stopped with why, and offers a comment and its issue, never Merge", async () => {
		open(list([stopped()]));

		const group = await stoppedGroup();
		const card = within(
			group.getByRole("article", { name: "Show the empty state" }),
		);
		expect(card.getByText("Stopped")).toBeTruthy();
		expect(card.getByText("Adamniels/Fodr")).toBeTruthy();
		expect(card.getByText("#12")).toBeTruthy();
		expect(card.getByText("Why it stopped")).toBeTruthy();
		expect(card.getByText(REASON)).toBeTruthy();
		expect(card.getByRole("button", { name: RUN_AGAIN })).toBeTruthy();
		expect(
			card.getByRole("link", { name: "Open on GitHub" }).getAttribute("href"),
		).toBe("https://github.com/Adamniels/Fodr/issues/12");
		expect(card.queryByRole("button", { name: "Merge" })).toBeNull();
	});

	test("Comment to run again posts the owner's comment on the card, and the list is read again", async () => {
		const { sendBackCard } = open(list([stopped()]), { after: list([]) });

		const group = await stoppedGroup();
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

		const group = await stoppedGroup();
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

		const group = await stoppedGroup();
		expect(group.queryByRole("link", { name: "Open on GitHub" })).toBeNull();
	});
});

describe("a project that cannot run its cards", () => {
	test("shows under Stopped with its message, counted with the stopped cards", async () => {
		open(list([stopped()], [failing()]));

		const group = await stoppedGroup();
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

		expect(await stoppedGroup()).toBeTruthy();
		expect(screen.queryByText("Nothing waits for you")).toBeNull();
	});
});
