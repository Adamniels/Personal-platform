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
import { App } from "./app";
import type { WaitingApi, WaitingList } from "./waiting";
import type { FeatureItem } from "./waiting-feature";

const NOW = Date.parse("2026-11-20T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const HEAD = "9".repeat(40);

function feature(overrides: Partial<FeatureItem> = {}): FeatureItem {
	return {
		project_id: PROJECT,
		project_name: "Fodr",
		owner: "Adamniels",
		number: 38,
		title: "Feeding plan, version 2",
		web_url: "https://github.com/Adamniels/Fodr/issues/38",
		state: "ready",
		reason: null,
		pull_request_number: 51,
		pull_request_url: "https://github.com/Adamniels/Fodr/pull/51",
		review: {
			verdict: "approve",
			summary: "The plan and its page fit together.",
			blocking: 0,
			commit: HEAD,
		},
		unanswered: 2,
		changed_at: new Date(NOW - 60_000).toISOString(),
		merge: null,
		...overrides,
	};
}

function list(
	features: FeatureItem[],
	cards: WaitingList["cards"] = [],
): WaitingList {
	return {
		count: features.length + cards.length,
		needs_relink: false,
		stopped_cards: [],
		refused_cards: [],
		failing_projects: [],
		features,
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
		cards,
		decisions: [],
	};
}

function openWith(answer: WaitingList) {
	const waiting: WaitingApi = {
		loadWaiting: () => Promise.resolve(answer),
		refresh: () => Promise.resolve(),
	};
	window.history.pushState(null, "", "/devflow");
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

/** The list with the owner's GitHub linked, and `sendBackFeature` as the
 * actions' way to send a feature back. */
function openLinked(
	answer: WaitingList,
	sendBackFeature: ActionsApi["sendBackFeature"],
) {
	const waiting: WaitingApi = {
		loadWaiting: vi.fn(() => Promise.resolve(answer)),
		refresh: () => Promise.resolve(),
	};
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
				disconnect: () => Promise.resolve(),
			}}
			waiting={waiting}
			actions={{
				merge: () => Promise.resolve(),
				sendBack: () => Promise.resolve(),
				resume: () => Promise.resolve(),
				mergeCard: () => Promise.resolve(),
				sendBackCard: () => Promise.resolve(),
				loadFeatureMerge: () => Promise.reject(new Error("not offered")),
				mergeFeature: () => Promise.resolve(),
				sendBackFeature,
			}}
			now={() => NOW}
		/>,
	);
	return waiting;
}

/** The list with `answer`, read as `answers` says after the first read, the
 * caller's GitHub linked, merging as `actions` does. */
function openActing(
	answer: WaitingList,
	after: WaitingList,
	actions: Partial<ActionsApi>,
) {
	const loadWaiting = vi
		.fn<WaitingApi["loadWaiting"]>()
		.mockResolvedValueOnce(answer)
		.mockResolvedValue(after);
	window.history.pushState(null, "", "/devflow");
	render(
		<App
			loadStatus={() =>
				Promise.resolve({
					code_host: "fake",
					github: { state: "linked", login: "octo-owner" },
				})
			}
			waiting={{ loadWaiting, refresh: () => Promise.resolve() }}
			actions={{
				merge: () => Promise.resolve(),
				sendBack: () => Promise.resolve(),
				resume: () => Promise.resolve(),
				mergeCard: () => Promise.resolve(),
				sendBackCard: () => Promise.resolve(),
				loadFeatureMerge: () => Promise.reject(new Error("not offered")),
				mergeFeature: () => Promise.resolve(),
				sendBackFeature: () => Promise.resolve(),
				...actions,
			}}
			now={() => NOW}
		/>,
	);
}

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close. These stand in for the browser's, as projects-link.test.tsx's do.
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
	this.open = true;
	this.querySelector("button")?.focus();
};
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
	if (!this.open) return;
	this.open = false;
	this.dispatchEvent(new Event("close"));
};

function panel() {
	return within(screen.getByRole("tabpanel"));
}

afterEach(cleanup);

describe("features ready for your review", () => {
	test("a ready feature shows in its own group, with its review and its decisions", async () => {
		openWith(list([feature()]));

		const group = await panel().findByRole("region", {
			name: "Features ready for your review",
		});
		expect(within(group).getByText("1")).toBeTruthy();
		const item = within(
			within(group).getByRole("article", { name: "Feeding plan, version 2" }),
		);
		expect(item.getByText("Ready for you")).toBeTruthy();
		expect(item.getByText("Adamniels/Fodr")).toBeTruthy();
		expect(item.getByText("Pull request #51")).toBeTruthy();
		expect(item.getByText("The plan and its page fit together.")).toBeTruthy();
		expect(item.getByText(HEAD.slice(0, 7))).toBeTruthy();
		expect(
			item.getByText(
				"2 decisions its cards marked for you are not answered yet.",
			),
		).toBeTruthy();
		expect(
			item.getByRole("link", { name: "Open on GitHub" }).getAttribute("href"),
		).toBe("https://github.com/Adamniels/Fodr/pull/51");
	});

	test("its page is one click away, inside the app", async () => {
		openWith(list([feature()]));

		const open = await panel().findByRole("link", { name: "Open the feature" });
		expect(open.getAttribute("href")).toBe(
			`/devflow/projects/${PROJECT}/features/38`,
		);
		fireEvent.click(open);
		expect(window.location.pathname).toBe(
			`/devflow/projects/${PROJECT}/features/38`,
		);
	});

	test("a feature waiting for you shows in the same group, with why", async () => {
		openWith(
			list([
				feature({
					state: "waiting_for_owner",
					reason: "The review of the whole feature asked for changes",
					review: {
						verdict: "changes",
						summary: "Two cards read the plan twice.",
						blocking: 1,
						commit: HEAD,
					},
					unanswered: 0,
				}),
			]),
		);

		const group = await panel().findByRole("region", {
			name: "Features ready for your review",
		});
		const item = within(within(group).getByRole("article"));
		expect(item.getByText("Waiting for you")).toBeTruthy();
		expect(
			item.getByText("The review of the whole feature asked for changes"),
		).toBeTruthy();
		expect(item.getByText(/1 finding blocks it/)).toBeTruthy();
		expect(
			item.getByText("Every decision its cards marked for you is answered."),
		).toBeTruthy();
	});

	test("the group comes after the pull requests waiting and before what stopped", async () => {
		openWith({
			...list([feature()]),
			stopped_cards: [
				{
					project_id: PROJECT,
					project_name: "Fodr",
					owner: "Adamniels",
					number: 7,
					title: "A stopped card",
					web_url: "https://github.com/Adamniels/Fodr/issues/7",
					reason: "The check failed with exit code 1",
					stopped_at: new Date(NOW - 60_000).toISOString(),
					pull_request_url: null,
				},
			],
		});

		await panel().findByRole("region", {
			name: "Features ready for your review",
		});
		expect(
			panel()
				.getAllByRole("region")
				.map((region) => region.getAttribute("aria-labelledby"))
				.map((id) => document.getElementById(id ?? "")?.textContent),
		).toEqual(["Features ready for your review", "Stopped"]);
	});

	test("a hostile title and reason are text", async () => {
		const hostile = "<img src=x onerror=alert(1)>";
		openWith(
			list([feature({ title: `Plan ${hostile}`, reason: `Why ${hostile}` })]),
		);

		const item = await panel().findByRole("article", {
			name: `Plan ${hostile}`,
		});
		expect(item.querySelector("img")).toBeNull();
		expect(within(item).getByText(`Why ${hostile}`)).toBeTruthy();
	});

	test("a feature offered for merging is merged from its item, after a confirmation", async () => {
		const mergeFeature = vi.fn<ActionsApi["mergeFeature"]>(() =>
			Promise.resolve(),
		);
		openActing(
			list([feature({ merge: { into: "trunk", head_commit: HEAD } })]),
			list([]),
			{
				loadFeatureMerge: () =>
					Promise.resolve({
						number: 38,
						title: "Feeding plan, version 2",
						into: "trunk",
						pull_request: {
							number: 51,
							url: "https://github.com/Adamniels/Fodr/pull/51",
						},
						head_commit: HEAD,
						review: "approved",
						open_findings: [],
						unanswered: 2,
					}),
				mergeFeature,
			},
		);

		const item = within(
			await panel().findByRole("article", { name: "Feeding plan, version 2" }),
		);
		// Named with the default branch, beside the item's links.
		const button = item.getByRole("button", { name: "Merge into trunk" });
		expect(item.getByRole("link", { name: "Open the feature" })).toBeTruthy();
		fireEvent.click(button);

		const dialog = within(
			await screen.findByRole("dialog", {
				name: "Merge feature #38 into trunk?",
			}),
		);
		expect(
			dialog.getByText("Merging confirms 2 decisions you have not answered."),
		).toBeTruthy();
		fireEvent.click(dialog.getByRole("button", { name: "Merge" }));

		await waitFor(() =>
			expect(mergeFeature).toHaveBeenCalledWith(PROJECT, 38, HEAD),
		);
		expect(await screen.findByText("Merged feature #38")).toBeTruthy();
		await waitFor(() =>
			expect(
				panel().queryByRole("article", { name: "Feeding plan, version 2" }),
			).toBeNull(),
		);
	});

	test("a feature not offered for merging has no Merge", async () => {
		openWith(list([feature({ state: "waiting_for_owner", merge: null })]));

		const item = within(
			await panel().findByRole("article", { name: "Feeding plan, version 2" }),
		);
		expect(item.queryByRole("button", { name: /^Merge into/ })).toBeNull();
		expect(item.getByRole("link", { name: "Open the feature" })).toBeTruthy();
	});

	test("a feature is sent back from the list with a comment", async () => {
		const sendBackFeature = vi.fn<ActionsApi["sendBackFeature"]>(() =>
			Promise.resolve(),
		);
		const waiting = openLinked(
			list([
				feature({
					state: "waiting_for_owner",
					reason: "Review rounds used up",
				}),
			]),
			sendBackFeature,
		);

		const item = within(
			await panel().findByRole("article", { name: "Feeding plan, version 2" }),
		);
		fireEvent.click(
			await item.findByRole("button", { name: "Send the whole feature back" }),
		);
		fireEvent.change(
			item.getByLabelText(
				"What should the developer change in the whole feature?",
			),
			{ target: { value: "Round the portions once." } },
		);
		fireEvent.click(item.getByRole("button", { name: "Send" }));

		await waitFor(() =>
			expect(sendBackFeature).toHaveBeenCalledWith(
				PROJECT,
				38,
				"Round the portions once.",
			),
		);
		await waitFor(() => expect(waiting.loadWaiting).toHaveBeenCalledTimes(2));
	});

	test("sending back waits for the GitHub link", async () => {
		openWith(list([feature()]));

		const item = within(
			await panel().findByRole("article", { name: "Feeding plan, version 2" }),
		);
		expect(
			(
				item.getByRole("button", {
					name: "Send the whole feature back",
				}) as HTMLButtonElement
			).disabled,
		).toBe(true);
	});
});
