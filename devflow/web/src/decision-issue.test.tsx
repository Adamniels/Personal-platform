import {
	cleanup,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { App } from "./app";
import type { DecisionItem } from "./decision-issue";
import type { Status } from "./status";
import type { WaitingList } from "./waiting";

const NOW = Date.parse("2026-11-19T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const STATUS: Status = {
	code_host: "fake",
	github: { state: "linked", login: "octo-owner" },
};

function decision(overrides: Partial<DecisionItem> = {}): DecisionItem {
	return {
		id: "22222222-2222-2222-2222-222222222222",
		project_id: PROJECT,
		project_name: "Fodr",
		owner: "Adamniels",
		card_number: 4,
		card_title: "Add the empty state",
		card_url: "https://github.com/Adamniels/Fodr/issues/4",
		pull_request_number: 7,
		pull_request_url: "https://github.com/Adamniels/Fodr/pull/7",
		issue_number: 9,
		issue_url: "https://github.com/Adamniels/Fodr/issues/9",
		chosen: "Show it on every list",
		alternative: "Only on habits",
		why: "The lists look the same",
		reviewer_view: "Agreed: the lists share one component.",
		opened_at: "2026-11-19T11:55:00+00:00",
		...overrides,
	};
}

function list(decisions: DecisionItem[]): WaitingList {
	return {
		count: decisions.length,
		needs_relink: false,
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
		stopped_cards: [],
		failing_projects: [],
		features: [],
		decisions,
	};
}

function open(decisions: DecisionItem[]) {
	window.history.pushState(null, "", "/devflow");
	render(
		<App
			loadStatus={() => Promise.resolve(STATUS)}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
				disconnect: () => Promise.resolve(),
			}}
			waiting={{
				loadWaiting: () => Promise.resolve(list(decisions)),
				refresh: () => Promise.resolve(),
			}}
			actions={{
				merge: () => Promise.resolve(),
				sendBack: () => Promise.resolve(),
				resume: () => Promise.resolve(),
				mergeCard: () => Promise.resolve(),
				sendBackCard: () => Promise.resolve(),
				loadFeatureMerge: () => Promise.reject(new Error("not offered")),
				mergeFeature: () => Promise.resolve(),
				sendBackFeature: () => Promise.resolve(),
			}}
			now={() => NOW}
		/>,
	);
}

afterEach(cleanup);

describe("a decision to look at", () => {
	test("is under Decisions to look at, counted, with what was decided and where", async () => {
		open([decision()]);

		const region = await screen.findByRole("region", {
			name: /Decisions to look at/,
		});
		const shown = within(
			within(region).getByRole("article", { name: "Show it on every list" }),
		);
		expect(screen.getByRole("tab", { name: "Waiting for you 1" })).toBeTruthy();
		expect(shown.getByText("To look at")).toBeTruthy();
		expect(shown.getByText("Adamniels/Fodr")).toBeTruthy();
		expect(shown.getByText("Only on habits")).toBeTruthy();
		expect(shown.getByText("The lists look the same")).toBeTruthy();
		expect(
			shown.getByText("Agreed: the lists share one component."),
		).toBeTruthy();
		expect(shown.getByText(/Merged in #7/)).toBeTruthy();
		const link = shown.getByRole("link", { name: "Open on GitHub" });
		expect(link.getAttribute("href")).toBe(
			"https://github.com/Adamniels/Fodr/issues/9",
		);
		expect(link.getAttribute("rel")).toBe("noreferrer");
	});

	test("says when the reviewer gave no view, and leaves out what the agent did not write", async () => {
		open([decision({ reviewer_view: null, alternative: null, why: null })]);

		const shown = within(
			await screen.findByRole("article", { name: "Show it on every list" }),
		);
		expect(shown.getByText("The reviewer gave no view on it.")).toBeTruthy();
		expect(shown.queryByText("Instead of")).toBeNull();
		expect(shown.queryByText("Because")).toBeNull();
	});

	test("shows what the agent wrote as text, and links only https", async () => {
		open([
			decision({
				chosen: '<img src=x onerror="alert(1)">',
				issue_url: "javascript:alert(1)",
			}),
		]);

		await waitFor(() =>
			expect(
				screen.getByRole("article", { name: '<img src=x onerror="alert(1)">' }),
			).toBeTruthy(),
		);
		expect(document.querySelector("img[onerror]")).toBeNull();
		expect(screen.queryByRole("link", { name: "Open on GitHub" })).toBeNull();
	});
});
