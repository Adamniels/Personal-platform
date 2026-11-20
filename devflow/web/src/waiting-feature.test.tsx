import {
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
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
});
