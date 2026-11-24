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
		repository: "Adamniels/Fodr",
		title: "Add the empty state",
		web_url: "https://github.com/Adamniels/Fodr/issues/4",
		state: "waiting_for_owner",
		reason: "Waiting for CI to report on its commit",
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
				stderr_tail: null,
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
				stderr_tail: "",
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
		merge: null,
		feature: null,
		most_rounds: 3,
		reading: null,
		earlier_readings: [],
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

	test("links to its feature's page when it is a card of one", async () => {
		open(() =>
			Promise.resolve(
				story({ feature: { number: 10, title: "Dev flow: features" } }),
			),
		);

		const link = await screen.findByRole("link", {
			name: "Feature #10: Dev flow: features",
		});
		expect(link.getAttribute("href")).toBe(
			`/devflow/projects/${PROJECT}/features/10`,
		);
		expect(link.getAttribute("target")).toBeNull();
	});

	test("shows what the card was read as: each question with the answer it is built on", async () => {
		open(() =>
			Promise.resolve(
				story({
					reading: {
						outcome: "questions",
						questions: [
							{
								question: "Is the list sorted by name or by `date`?",
								recommendation: "By date, newest first",
								why: "The page beside it sorts so",
								answer: "By date, newest first",
								recommended: true,
							},
						],
						missing: [],
						read_at: "2026-11-18T09:00:00+00:00",
						commented: true,
						answered: false,
					},
				}),
			),
		);

		const reading = await screen.findByRole("article", { name: "The reading" });
		expect(
			within(reading).getByText("Built on the recommendations"),
		).toBeTruthy();
		expect(reading.textContent).toContain("Dev flow posted them on the card.");
		const questions = within(reading).getByRole("list", { name: "Questions" });
		expect(within(questions).getAllByRole("listitem")).toHaveLength(1);
		expect(questions.textContent).toContain(
			"Is the list sorted by name or by date?",
		);
		expect(questions.textContent).toContain("Answer By date, newest first");
		expect(questions.textContent).toContain(
			"Because The page beside it sorts so",
		);
		expect(within(reading).queryByRole("list", { name: "Missing" })).toBeNull();
	});

	test("a card that needs answers says it waits for them, with the recommendations", async () => {
		open(() =>
			Promise.resolve(
				story({
					state: "needs_answers",
					reason: "Waits for your answers to its questions",
					reading: {
						outcome: "questions",
						questions: [
							{
								question: "Is the list sorted by name or by date?",
								recommendation: "By date, newest first",
								why: "The page beside it sorts so",
								answer: "By date, newest first",
								recommended: true,
							},
						],
						missing: [],
						read_at: "2026-11-18T09:00:00+00:00",
						commented: false,
						answered: false,
					},
				}),
			),
		);

		const reading = await screen.findByRole("article", { name: "The reading" });
		expect(within(reading).getByText("Waits for your answers")).toBeTruthy();
		expect(reading.textContent).toContain(
			"It waits for your answers under Questions before starting on Waiting for you",
		);
		const questions = within(reading).getByRole("list", { name: "Questions" });
		expect(questions.textContent).toContain(
			"Recommended By date, newest first",
		);
	});

	test("a card the owner answered shows his answers, the accepted ones marked", async () => {
		open(() =>
			Promise.resolve(
				story({
					reading: {
						outcome: "questions",
						questions: [
							{
								question: "Is the list sorted by name or by date?",
								recommendation: "By date, newest first",
								why: "The page beside it sorts so",
								answer: "By date, newest first",
								recommended: true,
							},
							{
								question: "Does an empty list show a sentence?",
								recommendation: "A sentence",
								why: "Nothing looks broken",
								answer: "Nothing at all",
								recommended: false,
							},
						],
						missing: [],
						read_at: "2026-11-18T09:00:00+00:00",
						commented: true,
						answered: true,
					},
				}),
			),
		);

		const reading = await screen.findByRole("article", { name: "The reading" });
		expect(within(reading).getByText("Built on your answers")).toBeTruthy();
		const items = within(
			within(reading).getByRole("list", { name: "Questions" }),
		).getAllByRole("listitem");
		expect(items[0]?.textContent).toContain(
			"Answer By date, newest first (recommended)",
		);
		expect(items[1]?.textContent).toContain("Answer Nothing at all");
		expect(items[1]?.textContent).not.toContain("(recommended)");
	});

	test("a card read as too thin lists what it lacks, and one read as ready nothing more", async () => {
		open(() =>
			Promise.resolve(
				story({
					state: "refused",
					reason: "Too thin to build; it lacks: Which page shows the list",
					reading: {
						outcome: "too_thin",
						questions: [],
						missing: ["Which page shows the list"],
						read_at: "2026-11-18T09:00:00+00:00",
						commented: false,
						answered: false,
					},
				}),
			),
		);

		const reading = await screen.findByRole("article", { name: "The reading" });
		expect(within(reading).getAllByText("Too thin to build")).toHaveLength(1);
		expect(
			within(reading).getByRole("list", { name: "Missing" }).textContent,
		).toBe("Which page shows the list");
		expect(
			within(reading).queryByRole("list", { name: "Questions" }),
		).toBeNull();
		cleanup();

		open(() =>
			Promise.resolve(
				story({
					reading: {
						outcome: "ready",
						questions: [],
						missing: [],
						read_at: "2026-11-18T09:00:00+00:00",
						commented: false,
						answered: false,
					},
				}),
			),
		);
		const ready = await screen.findByRole("article", { name: "The reading" });
		expect(within(ready).getByText("Ready to build")).toBeTruthy();
		expect(within(ready).queryAllByRole("list")).toHaveLength(0);
	});

	test("a card read again keeps its earlier readings, with the answers given to them", async () => {
		open(() =>
			Promise.resolve(
				story({
					state: "needs_answers",
					reason: "Waits for your answers to its questions",
					reading: {
						outcome: "questions",
						questions: [
							{
								question: "Is the count shown?",
								recommendation: "Yes",
								why: "It is one line",
								answer: "Yes",
								recommended: true,
							},
						],
						missing: [],
						read_at: "2026-11-19T09:00:00+00:00",
						commented: false,
						answered: false,
					},
					earlier_readings: [
						{
							outcome: "questions",
							questions: [
								{
									question: "Is the list sorted by date?",
									recommendation: "By date",
									why: "Newest matters",
									answer: "By name",
									recommended: false,
								},
							],
							missing: [],
							read_at: "2026-11-18T09:00:00+00:00",
							commented: true,
							answered: true,
						},
						{
							outcome: "too_thin",
							questions: [],
							missing: ["Which page shows the list"],
							read_at: "2026-11-17T09:00:00+00:00",
							commented: true,
							answered: false,
						},
					],
				}),
			),
		);

		const reading = await screen.findByRole("article", { name: "The reading" });
		// The newest reading is the one shown first, with its own question.
		expect(
			within(reading).getByRole("list", { name: "Questions" }).textContent,
		).toContain("Is the count shown?");
		const earlier = within(reading).getByRole("list", {
			name: "Earlier readings",
		});
		const rows = within(earlier).getAllByRole("listitem", {
			name: /^Read on /,
		});
		expect(rows).toHaveLength(2);
		const [answered, thin] = rows as [HTMLElement, HTMLElement];
		expect(within(answered).getByText("Answered")).toBeTruthy();
		const then = within(answered).getByRole("list", { name: "Questions then" });
		expect(then.textContent).toContain("Is the list sorted by date?");
		expect(then.textContent).toContain("Your answer By name");
		expect(within(thin).getByText("Too thin")).toBeTruthy();
		expect(
			within(thin).getByRole("list", { name: "Missing then" }).textContent,
		).toBe("Which page shows the list");
	});

	test("a card read once shows no earlier readings", async () => {
		open(() =>
			Promise.resolve(
				story({
					reading: {
						outcome: "ready",
						questions: [],
						missing: [],
						read_at: "2026-11-18T09:00:00+00:00",
						commented: false,
						answered: false,
					},
				}),
			),
		);
		const reading = await screen.findByRole("article", { name: "The reading" });
		expect(
			within(reading).queryByRole("list", { name: "Earlier readings" }),
		).toBeNull();
	});

	test("a card not read yet shows no reading", async () => {
		open(() => Promise.resolve(story()));
		await screen.findByRole("heading", { name: "Add the empty state" });
		expect(screen.queryByRole("article", { name: "The reading" })).toBeNull();
	});

	test("names no feature when it is a card on its own", async () => {
		open(() => Promise.resolve(story()));
		await screen.findByRole("heading", { name: "Add the empty state" });
		expect(screen.queryByRole("link", { name: /^Feature #/ })).toBeNull();
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
		expect(
			screen.getByText("Waiting for CI to report on its commit"),
		).toBeTruthy();
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

	test("marks each page the agent did not look at", async () => {
		const shown = story();
		if (shown.report) {
			shown.report.pages = [
				{ path: "/habits", why: "The empty state shows there", looked: true },
				{ path: "/settings", why: "The toggle moved", looked: false },
			];
		}
		open(() => Promise.resolve(shown));

		const report = within(
			await screen.findByRole("article", { name: "The latest report" }),
		);
		const pages = within(report.getByRole("list", { name: "Pages" }))
			.getAllByRole("listitem")
			.map((item) => item.textContent);
		expect(pages).toEqual([
			"/habits Looked atThe empty state shows there",
			"/settings The agent did not look at this pageThe toggle moved",
		]);
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

	test("counts each round against the project's fix rounds", async () => {
		open(() => Promise.resolve(story({ ...inRoundTwo(), most_rounds: 5 })));

		const rounds = within(await screen.findByRole("list", { name: "Rounds" }));
		expect(rounds.getByText("Round 2 of 5")).toBeTruthy();
		expect(rounds.getByText("Round 1 of 5")).toBeTruthy();
		expect(rounds.queryByText(/of 3$/)).toBeNull();
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

	test("a card Dev flow merged says so, with the merge commit and the commit approved", async () => {
		open(() =>
			Promise.resolve(
				story({
					state: "done",
					reason: null,
					merge: {
						commit: `def5678${"0".repeat(33)}`,
						approved: HEAD,
						base: "main",
						merged_at: "2026-11-18T10:30:00+00:00",
					},
				}),
			),
		);

		const merge = within(
			await screen.findByRole("article", { name: "The merge" }),
		);
		expect(
			merge.getByText(/Dev flow merged #7 into main as the app/),
		).toBeTruthy();
		expect(merge.getByText("def5678").tagName).toBe("CODE");
		expect(merge.getByText("abc1234").tagName).toBe("CODE");
	});

	test("a card with no merge of Dev flow's shows none", async () => {
		open(() => Promise.resolve(story()));

		await screen.findByRole("heading", { name: "Add the empty state" });
		expect(screen.queryByRole("article", { name: "The merge" })).toBeNull();
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

/** A run of the card, a build that succeeded at HEAD unless given otherwise. */
function run(overrides: Partial<CardStory["runs"][number]>) {
	return {
		id: "run",
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
		stderr_tail: null,
		...overrides,
	};
}

/** A card built at HEAD, reviewed with changes, fixed to FIRST in round 1,
 * and approved at FIRST: four entries, given newest first as the story is. */
function builtFixedApproved(): Partial<CardStory> {
	return {
		state: "waiting_for_owner",
		reason: "Approved, and waits for you",
		runs: [
			run({
				id: "r2",
				kind: "review",
				started_at: "2026-11-18T10:20:00+00:00",
				check: null,
				head_commit: FIRST,
			}),
			run({
				id: "f1",
				kind: "fix",
				started_at: "2026-11-18T10:11:00+00:00",
				head_commit: FIRST,
			}),
			run({
				id: "r1",
				kind: "review",
				started_at: "2026-11-18T10:05:00+00:00",
				check: null,
			}),
			run({ id: "b1", started_at: "2026-11-18T10:00:00+00:00" }),
		],
		reviews: [
			{
				run_id: "r2",
				commit: FIRST,
				verdict: "approve",
				summary: "The empty state is there and tested.",
				findings: [],
				for_owner: [],
				reviewed_at: "2026-11-18T10:22:00+00:00",
				posted: true,
				current: true,
			},
			{
				run_id: "r1",
				commit: HEAD,
				verdict: "changes",
				summary: "The empty state shows only after a reload.",
				findings: [
					{
						blocking: false,
						file: "web/src/list.css",
						line: 3,
						what: "A colour by hand",
						why: "",
					},
					{
						blocking: true,
						file: "web/src/list.tsx",
						line: 42,
						what: "The empty state is read once",
						why: "A list emptied later shows nothing",
					},
				],
				for_owner: [],
				reviewed_at: "2026-11-18T10:07:00+00:00",
				posted: true,
				current: false,
			},
		],
		rounds: [
			{
				run_id: "f1",
				round: 1,
				review_run_id: "r1",
				comments: [],
				result: "succeeded",
				replies: [
					{
						finding: "The empty state is read once",
						answer: "It is read on every `change` now.",
					},
				],
				fixed_commit: FIRST,
				review_after_run_id: "r2",
			},
		],
	};
}

/** The timeline's entries, its own items and none nested in them. */
async function entries(): Promise<HTMLElement[]> {
	const timeline = await screen.findByRole("list", { name: "How it got here" });
	return [...timeline.children].filter(
		(child): child is HTMLElement => child.tagName === "LI",
	);
}

/** Each entry's title, in the order shown. */
function titles(items: HTMLElement[]): (string | undefined)[] {
	return items.map((item) => item.querySelector("h3")?.textContent ?? undefined);
}

describe("a card's page tells its story in order", () => {
	test("runs, reviews and rounds are one timeline, oldest first, ending in now", async () => {
		open(() => Promise.resolve(story(builtFixedApproved())));

		const items = await entries();
		expect(titles(items)).toEqual([
			"Build",
			"Review of abc1234",
			"Fix, round 1 of 3",
			"Review of def5678",
			"Now",
		]);
		const [build, asked, fix, approved, now] = items.map((item) =>
			within(item),
		) as [
			ReturnType<typeof within>,
			ReturnType<typeof within>,
			ReturnType<typeof within>,
			ReturnType<typeof within>,
			ReturnType<typeof within>,
		];

		// A run: its kind and result, and its check with the output behind
		// Show output.
		expect(build.getByText("Succeeded")).toBeTruthy();
		expect(build.getByText(/Check exit 0 on/).textContent).toContain(
			"abc1234",
		);
		expect(build.queryByText("Tests: 12 passed")).toBeNull();
		fireEvent.click(build.getByRole("button", { name: "Show output" }));
		expect(build.getByText("Tests: 12 passed")).toBeTruthy();

		// A review that asked for changes: its blocking finding first, on an
		// inset, and that it is not for the head.
		expect(asked.getByText("Changes asked for")).toBeTruthy();
		expect(asked.getByText(/older commit/)).toBeTruthy();
		const findings = within(asked.getByRole("list", { name: "Findings" }))
			.getAllByRole("listitem")
			.map((finding) => finding.textContent);
		expect(findings[0]).toContain("Blocking");
		expect(findings[0]).toContain("The empty state is read once");
		expect(findings[1]).toContain("A colour by hand");

		// A round: counted against the project's rounds, the developer's
		// replies on an inset, and the commit it left.
		const replies = fix.getByRole("list", { name: "The developer's replies" });
		expect(replies.textContent).toContain("It is read on every change now.");
		expect(within(replies).getByText("change").tagName).toBe("CODE");
		expect(fix.getByText(/^It left/).textContent).toContain("def5678");

		// The approval counts for the head.
		expect(approved.getByText("Approved")).toBeTruthy();
		expect(approved.getByText(/pull request's head/)).toBeTruthy();

		// Now: the state, which needs the owner.
		expect(now.getByText("Waiting for you")).toBeTruthy();
		expect(items[4]?.textContent).toContain("Needs you");
	});

	test("a run under way ends the timeline with a running now", async () => {
		open(() =>
			Promise.resolve(
				story({
					state: "building",
					reason: null,
					runs: [run({ result: null, finished_at: null, duration_ms: null })],
				}),
			),
		);

		const items = await entries();
		const now = items.at(-1) as HTMLElement;
		expect(now.querySelector("h3")?.textContent).toBe("Now");
		expect(now.textContent).not.toContain("Needs you");
		expect(items[0]?.textContent).toContain("Running");
	});

	test("a failed run shows its error output behind a button", async () => {
		open(() =>
			Promise.resolve(
				story({
					state: "stopped",
					reason: "Claude Code exited with 1",
					report: null,
					runs: [
						run({
							result: "failed_in_card",
							reason: "Claude Code exited with 1",
							in_card: true,
							check: null,
							head_commit: null,
							stderr_tail: "Error: the token was refused\nat login (cli.js:12)",
						}),
					],
				}),
			),
		);

		const [failed] = (await entries()).map((item) => within(item));
		expect(failed?.getByText("Failed")).toBeTruthy();
		expect(failed?.getByText(/It lay in the card/)).toBeTruthy();
		expect(failed?.queryByText(/the token was refused/)).toBeNull();
		const show = failed?.getByRole("button", {
			name: "Show Claude Code's error output",
		}) as HTMLElement;
		expect(show.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(show);
		const output = failed?.getByText(/the token was refused/) as HTMLElement;
		expect(output.tagName).toBe("PRE");
		expect(output.textContent).toBe(
			"Error: the token was refused\nat login (cli.js:12)",
		);
	});

	test("a failed run with no error output has no button for it", async () => {
		open(() =>
			Promise.resolve(
				story({
					runs: [
						run({
							result: "failed_outside_card",
							reason: "Claude's API did not answer",
							in_card: false,
							check: null,
							head_commit: null,
							stderr_tail: "",
						}),
					],
				}),
			),
		);

		const [failed] = (await entries()).map((item) => within(item));
		expect(failed?.getByText(/It lay outside the card/)).toBeTruthy();
		expect(failed?.queryByRole("button", { name: /error output/ })).toBeNull();
	});

	test("the banner follows the state", async () => {
		const keys: Record<string, string> = {
			waiting_for_owner: `card/${PROJECT}/4`,
			stopped: `card/${PROJECT}/4`,
			refused: `refused/${PROJECT}/4`,
			needs_answers: `questions/${PROJECT}/4`,
		};
		for (const [state, key] of Object.entries(keys)) {
			open(() => Promise.resolve(story({ state })));
			const link = await screen.findByRole("link", {
				name: "Decide in Waiting for you",
			});
			expect(link.getAttribute("href")).toBe(
				`/devflow?item=${encodeURIComponent(key)}`,
			);
			expect(screen.getAllByText("Needs you").length).toBeGreaterThan(0);
			cleanup();
		}

		for (const state of [
			"queued",
			"reading",
			"building",
			"checking",
			"reviewing",
			"fixing",
			"held",
			"done",
			"ignored",
		]) {
			open(() => Promise.resolve(story({ state })));
			await screen.findByRole("heading", { name: "Add the empty state" });
			expect(
				screen.queryByRole("link", { name: "Decide in Waiting for you" }),
			).toBeNull();
			expect(screen.queryByText("Needs you")).toBeNull();
			cleanup();
		}
	});

	test("the banner says what each state needs", async () => {
		const shown = story();
		if (shown.report) {
			shown.report.decisions = [
				{ chosen: "One", alternative: "", why: "", for_owner: true },
				{ chosen: "Two", alternative: "", why: "", for_owner: true },
				{ chosen: "Three", alternative: "", why: "", for_owner: false },
			];
		}
		open(() => Promise.resolve(shown));
		expect(
			await screen.findByText(
				"It passed its check and its review, and waits for you.",
			),
		).toBeTruthy();
		expect(screen.getByText(/confirms the 2 decisions/)).toBeTruthy();
		cleanup();

		open(() =>
			Promise.resolve(
				story({ state: "stopped", reason: "The check failed with exit code 2" }),
			),
		);
		const banner = (
			await screen.findByRole("link", { name: "Decide in Waiting for you" })
		).closest("[data-banner]") as HTMLElement;
		expect(banner.textContent).toContain("The check failed with exit code 2");
		cleanup();

		const reading = {
			outcome: "questions",
			questions: [1, 2, 3].map((n) => ({
				question: `Question ${n}?`,
				recommendation: "Yes",
				why: "",
				answer: "Yes",
				recommended: true,
			})),
			missing: [],
			read_at: "2026-11-18T09:00:00+00:00",
			commented: false,
			answered: false,
		};
		open(() => Promise.resolve(story({ state: "needs_answers", reading })));
		expect(
			await screen.findByText("It has 3 questions for you before it is built."),
		).toBeTruthy();
		cleanup();

		open(() =>
			Promise.resolve(
				story({
					state: "refused",
					reading: {
						...reading,
						outcome: "too_thin",
						questions: [],
						missing: ["Which page shows the list", "What empty means"],
					},
				}),
			),
		);
		const refused = (
			await screen.findByRole("link", { name: "Decide in Waiting for you" })
		).closest("[data-banner]") as HTMLElement;
		expect(refused.textContent).toContain("Which page shows the list");
		expect(refused.textContent).toContain("What empty means");
	});

	test("what it was read as stays, and read runs are entries in the timeline", async () => {
		const read = (at: string, outcome: string) => ({
			outcome,
			questions: [],
			missing: outcome === "too_thin" ? ["Which page shows the list"] : [],
			read_at: at,
			commented: false,
			answered: false,
		});
		open(() =>
			Promise.resolve(
				story({
					reading: read("2026-11-18T09:30:00+00:00", "ready"),
					earlier_readings: [
						read("2026-11-18T09:10:00+00:00", "too_thin"),
						read("2026-11-18T09:00:00+00:00", "too_thin"),
					],
					runs: [
						run({ id: "b1", started_at: "2026-11-18T10:00:00+00:00" }),
						run({
							id: "rd2",
							kind: "read",
							started_at: "2026-11-18T09:29:00+00:00",
							check: null,
							head_commit: null,
						}),
						run({
							id: "rd1",
							kind: "read",
							started_at: "2026-11-18T09:09:00+00:00",
							check: null,
							head_commit: null,
						}),
					],
				}),
			),
		);

		const reading = await screen.findByRole("article", { name: "The reading" });
		const did = screen.getByRole("article", { name: "What the agent did" });
		expect(reading.compareDocumentPosition(did)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(within(reading).getByText("Ready to build")).toBeTruthy();

		// The earlier readings only when asked for.
		expect(
			within(reading).queryByRole("list", { name: "Earlier readings" }),
		).toBeNull();
		const more = within(reading).getByRole("button", {
			name: "Show the 2 earlier readings",
		});
		expect(more.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(more);
		expect(
			within(
				within(reading).getByRole("list", { name: "Earlier readings" }),
			).getAllByRole("listitem", { name: /^Read on / }),
		).toHaveLength(2);

		// Each read run is an entry, in its place.
		expect(titles(await entries())).toEqual([
			"Reading",
			"Reading",
			"Build",
			"Now",
		]);
	});

	test("the report comes first and its decisions for me before the rest", async () => {
		const shown = story(builtFixedApproved());
		if (shown.report) {
			shown.report.decisions = [
				{
					chosen: "Name it EmptyList",
					alternative: "",
					why: "",
					for_owner: false,
				},
				{
					chosen: "Show it on every list",
					alternative: "Only on habits",
					why: "The lists look the same",
					for_owner: true,
				},
				{
					chosen: "Keep the old test",
					alternative: "",
					why: "",
					for_owner: false,
				},
			];
		}
		open(() => Promise.resolve(shown));

		const did = await screen.findByRole("article", {
			name: "What the agent did",
		});
		const history = screen.getByRole("article", { name: "How it got here" });
		expect(did.compareDocumentPosition(history)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(within(did).getByText("empty state").tagName).toBe("CODE");

		// For you first; the rest only when asked for.
		const decisions = within(did).getByRole("list", { name: "Decisions" });
		const first = within(decisions)
			.getAllByRole("listitem")
			.map((item) => item.textContent);
		expect(first).toHaveLength(1);
		expect(first[0]).toContain("For you");
		expect(first[0]).toContain("Show it on every list");
		fireEvent.click(
			within(did).getByRole("button", {
				name: "Show the 2 it did not need you for",
			}),
		);
		const all = within(within(did).getByRole("list", { name: "Decisions" }))
			.getAllByRole("listitem")
			.map((item) => item.textContent);
		expect(all).toHaveLength(3);
		expect(all[0]).toContain("Show it on every list");
		expect(all[1]).toContain("Name it EmptyList");
		expect(all[1]).not.toContain("For you");

		// Tasks with their marks, Tests with their results, Pages and Follow ups.
		expect(
			within(did).getByRole("list", { name: "Tasks" }).textContent,
		).toContain("Not done");
		expect(
			within(did).getByRole("list", { name: "Tests" }).textContent,
		).toContain("passed");
		expect(within(did).getByRole("list", { name: "Pages" })).toBeTruthy();
		expect(within(did).getByRole("list", { name: "Follow ups" })).toBeTruthy();
	});

	test("the header names the card's repository, number and feature", async () => {
		open(() =>
			Promise.resolve(
				story({ feature: { number: 10, title: "Dev flow: features" } }),
			),
		);
		expect(
			await screen.findByText("ADAMNIELS/FODR · CARD #4 · FEATURE #10"),
		).toBeTruthy();
		cleanup();

		open(() => Promise.resolve(story()));
		expect(
			await screen.findByText("ADAMNIELS/FODR · CARD #4 · NO FEATURE"),
		).toBeTruthy();
	});

	test("at a glance: the landing first, then the check, review, runs, tokens and rounds", async () => {
		open(() =>
			Promise.resolve(
				story({
					...builtFixedApproved(),
					state: "done",
					merge: {
						commit: SECOND,
						approved: FIRST,
						base: "main",
						merged_at: "2026-11-18T10:30:00+00:00",
					},
				}),
			),
		);

		const glance = await screen.findByRole("article", { name: "At a glance" });
		const text = glance.textContent ?? "";
		expect(text.indexOf("Into")).toBeGreaterThan(-1);
		expect(text.indexOf("Into")).toBeLessThan(text.indexOf("The check"));
		expect(text).toContain("main");
		expect(within(glance).getByText("fed8765").tagName).toBe("CODE");
		expect(within(glance).getByText("Approved")).toBeTruthy();
		// Four runs of 3 min 4 s each.
		expect(text).toContain("4 · 12 min 17 s");
		expect(text).toContain("164 in · 24,452 out");
		expect(
			within(glance).getByRole("img", { name: "Fix rounds: 1 of 3 used" }),
		).toBeTruthy();
	});
});
