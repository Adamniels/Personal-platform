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
		reviews: [],
		screenshots: null,
		rounds: [],
		...overrides,
	};
}

const FIRST = `def5678${"0".repeat(33)}`;
const SECOND = `fed8765${"0".repeat(33)}`;

/** A card in its second round: built at HEAD, reviewed with changes, fixed
 * to FIRST, reviewed with changes again, and its second fix under way; and
 * before all of it, the owner's comment on an older round. */
function inRoundTwo(): Partial<CardStory> {
	const review = (
		run: string,
		commit: string,
		at: string,
		current: boolean,
	) => ({
		run_id: run,
		commit,
		verdict: "changes",
		summary: "The empty state shows only after a reload.",
		findings: [
			{
				blocking: true,
				file: "web/src/list.tsx",
				line: 42,
				what: "The empty state is read once",
				why: "A list emptied later shows nothing",
			},
		],
		for_owner: [],
		reviewed_at: at,
		posted: true,
		current,
	});
	return {
		state: "fixing",
		reason: "Round 2 of 3: the developer is fixing it",
		reviews: [
			review("r2", FIRST, "2026-11-18T11:00:00+00:00", true),
			review("r1", HEAD, "2026-11-18T10:10:00+00:00", false),
		],
		rounds: [
			{
				run_id: "f2",
				round: 2,
				review_run_id: "r2",
				comments: [],
				result: null,
				replies: [],
				fixed_commit: null,
				review_after_run_id: null,
			},
			{
				run_id: "f1",
				round: 1,
				review_run_id: "r1",
				comments: [],
				result: "succeeded",
				replies: [
					{
						finding: "The empty state is read once",
						answer: "Fixed: it is read on every `change`.",
					},
				],
				fixed_commit: FIRST,
				review_after_run_id: "r2",
			},
			{
				run_id: "f0",
				round: null,
				review_run_id: null,
				comments: ["Name it `EmptyList`.\nAs the others are."],
				result: "failed_in_card",
				replies: [],
				fixed_commit: SECOND,
				review_after_run_id: null,
			},
		],
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

	test("shows each review with its commit, verdict, findings and view", async () => {
		open(() =>
			Promise.resolve(
				story({
					reviews: [
						{
							run_id: "01a3185c-5000-7000-8000-000000000003",
							commit: HEAD,
							verdict: "changes",
							summary: "The empty state shows only after a reload.",
							findings: [
								{
									blocking: false,
									file: "web/src/list.css",
									line: 3,
									what: "A colour by hand",
									why: "The tokens hold it",
								},
								{
									blocking: true,
									file: "web/src/list.tsx",
									line: 42,
									what: "The empty state is read `once`",
									why: "A list emptied later shows nothing",
								},
								{
									blocking: true,
									file: "",
									line: 0,
									what: "No test covers an emptied list",
									why: "",
								},
							],
							for_owner: [
								{
									decision: "Show it on every list",
									view: "Agreed: the lists share one component.",
								},
							],
							reviewed_at: "2026-11-18T10:05:00+00:00",
							posted: true,
							current: true,
						},
						{
							run_id: "01a3185c-5000-7000-8000-000000000004",
							commit: `def5678${"0".repeat(33)}`,
							verdict: "approve",
							summary: "",
							findings: [],
							for_owner: [],
							reviewed_at: "2026-11-18T09:05:00+00:00",
							posted: false,
							current: false,
						},
					],
				}),
			),
		);

		const panel = within(
			await screen.findByRole("article", { name: "Reviews" }),
		);
		const [newest, older] = panel
			.getAllByRole("listitem")
			.filter(
				(each) => each.parentElement?.getAttribute("aria-label") === "Reviews",
			)
			.map((each) => within(each));
		expect(newest?.getByText("abc1234").tagName).toBe("CODE");
		expect(newest?.getByText("Changes asked for")).toBeTruthy();
		expect(newest?.getByText("For the pull request's head")).toBeTruthy();
		expect(newest?.queryByText("Not on the pull request yet")).toBeNull();
		expect(
			newest?.getByText("The empty state shows only after a reload."),
		).toBeTruthy();

		// Blocking findings first, each with where it is, what and why.
		const findings = within(
			newest?.getByRole("list", { name: "Findings" }) as HTMLElement,
		)
			.getAllByRole("listitem")
			.map((finding) => finding.textContent);
		expect(findings).toEqual([
			"Blocking web/src/list.tsx:42 The empty state is read onceBecause A list emptied later shows nothing",
			"Blocking No test covers an emptied list",
			"Not blocking web/src/list.css:3 A colour by handBecause The tokens hold it",
		]);
		expect(newest?.getByText("once").tagName).toBe("CODE");
		expect(
			newest?.getByRole("list", { name: "For the owner" }).textContent,
		).toBe("Show it on every listAgreed: the lists share one component.");

		// A review of an older commit says it does not count.
		expect(older?.getByText("Approved")).toBeTruthy();
		expect(
			older?.getByText("For an older commit, so it does not count"),
		).toBeTruthy();
		expect(older?.getByText("Not on the pull request yet")).toBeTruthy();
		expect(older?.queryByRole("list", { name: "Findings" })).toBeNull();
	});

	test("shows the pictures of its head commit, paired, with the worker's note", async () => {
		open(() =>
			Promise.resolve(
				story({
					screenshots: {
						commit: HEAD,
						groups: [
							{ name: "habits", images: ["habits-desktop", "habits-phone"] },
						],
						note: "Screenshots could not be taken: /broken at desktop size: it took longer than 60 seconds",
					},
				}),
			),
		);

		const panel = within(
			await screen.findByRole("article", { name: "Screenshots" }),
		);
		expect(panel.getByText("abc1234").tagName).toBe("CODE");
		expect(
			panel.getByText(
				"Screenshots could not be taken: /broken at desktop size: it took longer than 60 seconds",
			),
		).toBeTruthy();
		const shots = within(panel.getByRole("list", { name: "Screenshots" }));
		expect(shots.getByText("habits")).toBeTruthy();
		expect(
			shots
				.getAllByRole("img")
				.map((image) => [image.getAttribute("alt"), image.getAttribute("src")]),
		).toEqual([
			[
				"habits-desktop",
				`/devflow/api/projects/${PROJECT}/cards/4/screenshots/habits-desktop`,
			],
			[
				"habits-phone",
				`/devflow/api/projects/${PROJECT}/cards/4/screenshots/habits-phone`,
			],
		]);
	});

	test("a card whose build took no pictures has no Screenshots", async () => {
		open(() => Promise.resolve(story()));

		await screen.findByRole("heading", { name: "Add the empty state" });
		expect(screen.queryByRole("article", { name: "Screenshots" })).toBeNull();
	});

	test("shows each round: what sent it back, the fix, its replies and the review after it", async () => {
		open(() => Promise.resolve(story(inRoundTwo())));

		const rounds = within(
			await screen.findByRole("list", { name: "Rounds" }),
		).getAllByRole("listitem", { name: /round|comment/i });
		expect(rounds).toHaveLength(3);
		const at = (index: number) => {
			const round = rounds[index];
			if (round === undefined) throw new Error(`no round ${index}`);
			return within(round);
		};
		const [second, first, comment] = [at(0), at(1), at(2)];

		// Newest first: round 2, still running, asked for by the second review.
		expect(second.getByText("Round 2 of 3")).toBeTruthy();
		expect(second.getByText("Running")).toBeTruthy();
		expect(second.getByText(/The review of/).textContent).toBe(
			"The review of def5678 asked for changes.",
		);
		expect(second.queryByRole("list", { name: "Replies" })).toBeNull();

		// Round 1: its review, its replies as text, the commit it left and the
		// review after it.
		expect(first.getByText("Round 1 of 3")).toBeTruthy();
		expect(first.getByText("Succeeded")).toBeTruthy();
		expect(first.getByText(/The review of/).textContent).toBe(
			"The review of abc1234 asked for changes.",
		);
		const replies = within(first.getByRole("list", { name: "Replies" }));
		expect(replies.getByText("The empty state is read once")).toBeTruthy();
		expect(replies.getByText("change").tagName).toBe("CODE");
		expect(first.getByText(/^It left/).textContent).toBe(
			"It left def5678, and the review of it asked for changes.",
		);

		// The owner's comment, not a round, his words as he wrote them.
		expect(comment.getByText("Your comment")).toBeTruthy();
		expect(comment.getByText("You sent it back with a comment.")).toBeTruthy();
		expect(
			comment.getByRole("list", { name: "What you wrote" }).textContent,
		).toBe("Name it `EmptyList`.\nAs the others are.");
		expect(comment.getByText("Failed")).toBeTruthy();
		expect(comment.getByText(/^It left/).textContent).toBe("It left fed8765.");
	});

	test("a card that was never sent back shows no rounds", async () => {
		open(() => Promise.resolve(story()));

		expect(
			await screen.findByRole("heading", { name: "Add the empty state" }),
		).toBeTruthy();
		expect(screen.queryByRole("list", { name: "Rounds" })).toBeNull();
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
		expect(screen.queryByRole("article", { name: "Reviews" })).toBeNull();
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
