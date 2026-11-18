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
import { type CardStory, took } from "./card";
import { cardAt, cardPath } from "./sections";
import type { Status } from "./status";

const PROJECT = "11111111-1111-1111-1111-111111111111";
const HEAD = `abc1234${"0".repeat(33)}`;

function story(overrides: Partial<CardStory> = {}): CardStory {
	return {
		number: 4,
		title: "Add the empty state",
		web_url: "https://github.com/Adamniels/Fodr/issues/4",
		state: "waiting_for_owner",
		reason: "Its pull request waits for you",
		pull_request: {
			number: 7,
			url: "https://github.com/Adamniels/Fodr/pull/7",
		},
		runs: [
			{
				id: "01a3185c-5000-7000-8000-000000000002",
				kind: "build",
				worker: "fodr-worker",
				started_at: "2026-11-18T10:00:00+00:00",
				finished_at: "2026-11-18T10:03:04+00:00",
				duration_ms: 184213,
				result: "succeeded",
				reason: null,
				in_card: null,
				auth_method: "oauth_token",
				input_tokens: 41,
				output_tokens: 6113,
				check: { exit_code: 0, output: "Tests: 12 passed" },
				head_commit: HEAD,
			},
			{
				id: "01a3185c-5000-7000-8000-000000000001",
				kind: "build",
				worker: "fodr-worker",
				started_at: "2026-11-18T09:00:00+00:00",
				finished_at: "2026-11-18T09:00:05+00:00",
				duration_ms: 5000,
				result: "failed_outside_card",
				reason: "Claude's API did not answer",
				in_card: false,
				auth_method: null,
				input_tokens: null,
				output_tokens: null,
				check: null,
				head_commit: null,
			},
		],
		report: {
			outcome: "done",
			stopped_reason: "",
			summary: "Added the `empty state`.",
			tasks: [
				{ text: "Add the empty state", done: true },
				{ text: "Count the habits", done: false },
			],
			decisions: [
				{
					chosen: "Show it on every list",
					alternative: "Only on habits",
					why: "The lists look the same",
					for_owner: true,
				},
			],
			tests: [{ what: "list.test.tsx", result: "passed" }],
			pages: [
				{ path: "/habits", why: "The empty state shows there", looked: true },
			],
			installed: [],
			follow_ups: ["Count the habits in the title"],
			replies: [],
			handover: "",
		},
		...overrides,
	};
}

function status(): Status {
	return {
		code_host: "fake",
		github: { state: "linked", login: "octo-owner" },
	};
}

/** The page at the card's address, its card read by `loadCard`. */
function open(
	loadCard: (project: string, number: number) => Promise<CardStory | null>,
) {
	window.history.pushState(null, "", cardPath(PROJECT, 4));
	const loads = vi.fn(loadCard);
	render(
		<App
			loadStatus={() => Promise.resolve(status())}
			waiting={{
				loadWaiting: () => new Promise(() => {}),
				refresh: () => Promise.resolve(),
			}}
			card={{ loadCard: loads }}
		/>,
	);
	return loads;
}

afterEach(cleanup);

describe("a card's page", () => {
	test("is under the Projects tab, at its own address", () => {
		expect(cardAt(`/devflow/projects/${PROJECT}/cards/4`)).toEqual({
			project: PROJECT,
			number: 4,
		});
		expect(cardAt(`/devflow/projects/${PROJECT}/cards/4/`)?.number).toBe(4);
		expect(cardAt(`/devflow/projects/${PROJECT}/cards/0`)).toBeUndefined();
		expect(cardAt(`/devflow/projects/${PROJECT}/cards/x`)).toBeUndefined();
		expect(
			cardAt(`/devflow/projects/${PROJECT}/cards/99999999999`),
		).toBeUndefined();
		expect(cardAt(`/devflow/projects/${PROJECT}`)).toBeUndefined();
	});

	test("tells the card's story: its state, links, runs and latest report", async () => {
		const loads = open(() => Promise.resolve(story()));

		expect(
			await screen.findByRole("heading", { name: "Add the empty state" }),
		).toBeTruthy();
		expect(loads).toHaveBeenCalledWith(PROJECT, 4);
		expect(
			screen.getByRole("tab", { name: "Projects", selected: true }),
		).toBeTruthy();
		expect(screen.getByText("Its pull request waits for you")).toBeTruthy();
		expect(
			screen.getByRole("link", { name: "Issue #4" }).getAttribute("href"),
		).toBe("https://github.com/Adamniels/Fodr/issues/4");
		const pull = screen.getByRole("link", { name: "Pull request #7" });
		expect(pull.getAttribute("href")).toBe(
			"https://github.com/Adamniels/Fodr/pull/7",
		);
		expect(pull.getAttribute("rel")).toBe("noreferrer");

		// Newest first, each with what the owner reads of it.
		const runs = within(
			screen.getByRole("list", { name: "Runs" }),
		).getAllByRole("listitem");
		expect(runs).toHaveLength(2);
		const [built, failed] = runs.map((run) => within(run));
		expect(built?.getByText("Build")).toBeTruthy();
		expect(built?.getByText("Succeeded")).toBeTruthy();
		expect(built?.getByText("took 3 min 4 s")).toBeTruthy();
		expect(built?.getByText("Logged in with oauth_token")).toBeTruthy();
		expect(built?.getByText("Tokens 41 in, 6,113 out")).toBeTruthy();
		expect(built?.getByText("Check exit 0 on abc1234")).toBeTruthy();
		expect(failed?.getByText("Failed")).toBeTruthy();
		expect(
			failed?.getByText(
				"Claude's API did not answer It lay outside the card, so it goes back in the queue.",
			),
		).toBeTruthy();
		expect(failed?.queryByRole("button", { name: "Show" })).toBeNull();

		// The check's output only when asked for.
		expect(built?.queryByText("Tests: 12 passed")).toBeNull();
		const show = built?.getByRole("button", { name: "Show" });
		expect(show?.getAttribute("aria-expanded")).toBe("false");
		if (show) fireEvent.click(show);
		expect(built?.getByText("Tests: 12 passed")).toBeTruthy();
		expect(
			built
				?.getByRole("button", { name: "Hide" })
				.getAttribute("aria-expanded"),
		).toBe("true");

		// The latest report, each list under its label.
		const report = within(
			screen.getByRole("article", { name: "The latest report" }),
		);
		expect(report.getByText("empty state").tagName).toBe("CODE");
		expect(report.getByRole("list", { name: "Tasks" }).textContent).toContain(
			"Not done Count the habits",
		);
		const decisions = within(report.getByRole("list", { name: "Decisions" }));
		expect(decisions.getByText("For you")).toBeTruthy();
		expect(decisions.getByText(/Instead of\s+Only on habits/)).toBeTruthy();
		expect(report.getByRole("list", { name: "Tests" }).textContent).toContain(
			"list.test.tsx: passed",
		);
		expect(report.getByRole("list", { name: "Pages" }).textContent).toContain(
			"/habits Looked at",
		);
		expect(report.getByRole("list", { name: "Follow ups" }).textContent).toBe(
			"Count the habits in the title",
		);
	});

	test("shows what came from the code host and the agent as text", async () => {
		open(() =>
			Promise.resolve(
				story({
					title: "<img src=x onerror=alert(1)>",
					web_url: "javascript:alert(1)",
					pull_request: { number: 7, url: "http://example.test/pull/7" },
				}),
			),
		);

		expect(
			await screen.findByRole("heading", {
				name: "<img src=x onerror=alert(1)>",
			}),
		).toBeTruthy();
		expect(screen.queryByRole("link", { name: "Issue #4" })).toBeNull();
		expect(screen.getByText("Issue #4")).toBeTruthy();
		expect(screen.queryByRole("link", { name: "Pull request #7" })).toBeNull();
		expect(document.querySelector("img")).toBeNull();
	});

	test("a card no run has taken says so, with no report", async () => {
		open(() =>
			Promise.resolve(
				story({
					state: "queued",
					reason: null,
					pull_request: null,
					runs: [],
					report: null,
				}),
			),
		);

		expect(
			await screen.findByText("No run has taken this card yet."),
		).toBeTruthy();
		expect(screen.getByText("Queued")).toBeTruthy();
		expect(
			screen.queryByRole("article", { name: "The latest report" }),
		).toBeNull();
		expect(screen.queryByText(/Pull request #/)).toBeNull();
	});

	test("a card the caller does not have is not found, with the way back", async () => {
		open(() => Promise.resolve(null));

		expect(await screen.findByText("There is no such card")).toBeTruthy();
		fireEvent.click(screen.getByRole("link", { name: "Back to the project" }));
		await waitFor(() =>
			expect(window.location.pathname).toBe(`/devflow/projects/${PROJECT}`),
		);
	});

	test("a card that cannot be read says so", async () => {
		open(() => Promise.reject(new Error("no")));

		expect((await screen.findByRole("alert")).textContent).toBe(
			"The card could not be read. Try again.",
		);
	});

	test("says how long a run took in words", () => {
		expect(took(5000)).toBe("5 s");
		expect(took(184213)).toBe("3 min 4 s");
		expect(took(2 * 3_600_000 + 5 * 60_000)).toBe("2 h 5 min");
	});
});
