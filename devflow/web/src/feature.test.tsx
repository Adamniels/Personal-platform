import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import { ApiError } from "./api/client";
import { App } from "./app";
import type { DecisionAnswer, FeatureApi, FeatureStory } from "./feature";
import { checkout, NOT_ANSWERED } from "./feature";
import own from "./feature.module.css";
import type { FeatureMergeConfirmation } from "./feature-merge";
import { cardPath, featureAt, featurePath } from "./sections";
import type { Status } from "./status";

const PROJECT = "11111111-1111-1111-1111-111111111111";
const BRANCH = "feature-38-feeding-plan-v2";
const COMMIT = `abc1234${"0".repeat(33)}`;
const RUN = "01a3185c-5000-7000-8000-000000000002";

function story(overrides: Partial<FeatureStory> = {}): FeatureStory {
	return {
		number: 38,
		repository: "Adamniels/Fodr",
		default_branch: "main",
		title: "Feeding plan, version 2",
		web_url: "https://github.com/Adamniels/Fodr/issues/38",
		state: "building",
		reason: null,
		stopped_by_owner: false,
		branch: { name: BRANCH, commit: COMMIT },
		pull_request: null,
		fix_rounds: 0,
		rounds: [],
		merge: null,
		most_rounds: 3,
		cards: [
			{
				number: 39,
				title: "Plan model and its rules",
				web_url: "https://github.com/Adamniels/Fodr/issues/39",
				state: "done",
				reason: null,
				fix_rounds: 1,
				input_tokens: 41,
				output_tokens: 6113,
				screenshots: {
					commit: COMMIT,
					groups: [{ name: "plan", images: ["plan-desktop", "plan-phone"] }],
					note: null,
				},
				decisions: [
					{
						run_id: RUN,
						position: 1,
						chosen: "Round amounts to `0.5 kg`",
						alternative: "0.1 kg, as the old plan showed",
						why: "A scale in a stable is not finer than that",
						reviewer_view: "Agrees.",
						answer: null,
					},
				],
				can_send_back: true,
				sent_back: null,
			},
			{
				number: 40,
				title: "The plan page",
				web_url: "https://github.com/Adamniels/Fodr/issues/40",
				state: "building",
				reason: null,
				fix_rounds: 0,
				input_tokens: 0,
				output_tokens: 0,
				screenshots: null,
				decisions: [],
				can_send_back: false,
				sent_back: null,
			},
		],
		runs: [],
		input_tokens: 0,
		output_tokens: 0,
		reviews: [],
		...overrides,
	};
}

function status(): Status {
	return {
		code_host: "fake",
		github: { state: "linked", login: "octo-owner" },
	};
}

/** The page at the feature's address, with `api`'s reads and answers. */
function open(api: Partial<FeatureApi> = {}) {
	window.history.pushState(null, "", featurePath(PROJECT, 38));
	const feature: FeatureApi = {
		loadFeature: vi.fn(() => Promise.resolve(story())),
		answerFine: vi.fn(() =>
			Promise.resolve<DecisionAnswer>({
				answer: "fine",
				answered_at: "2026-11-20T09:30:00+00:00",
			}),
		),
		loadFeatureMerge: vi.fn(() => Promise.reject(new Error("not offered"))),
		mergeFeature: vi.fn(() => Promise.resolve()),
		sendBack: vi.fn(() => Promise.resolve()),
		sendBackCard: vi.fn(() => Promise.resolve()),
		sendBackDecision: vi.fn(() => Promise.resolve()),
		runFeatureAgain: vi.fn(() => Promise.resolve()),
		copy: vi.fn(() => Promise.resolve()),
		...api,
	};
	render(
		<App
			loadStatus={() => Promise.resolve(status())}
			waiting={{
				loadWaiting: () => new Promise(() => {}),
				refresh: () => Promise.resolve(),
			}}
			feature={feature}
		/>,
	);
	return feature;
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

afterEach(cleanup);

/** Every review and round of the whole feature, opened in place in the
 * side column, once the feature is read. */
async function everyReviewAndRound(): Promise<HTMLElement> {
	const panel = await screen.findByRole("article", {
		name: "Reviews and rounds",
	});
	fireEvent.click(
		within(panel).getByRole("button", { name: "Read every review and round" }),
	);
	return within(panel).getByRole("list", { name: "Every review and round" });
}

/** The page's header, once the feature is read: its way back, its state and
 * caption, its title, its reason and its links. */
async function header(): Promise<HTMLElement> {
	const title = await screen.findByRole("heading", {
		level: 2,
		name: "Feeding plan, version 2",
	});
	const found = title.closest("header");
	if (found === null) throw new Error("the title is in the header");
	return found;
}

describe("a feature's page", () => {
	test("is under the Projects tab, at its own address", () => {
		expect(featureAt(`/devflow/projects/${PROJECT}/features/38`)).toEqual({
			project: PROJECT,
			number: 38,
		});
		expect(featureAt(`/devflow/projects/${PROJECT}/features/38/`)?.number).toBe(
			38,
		);
		expect(
			featureAt(`/devflow/projects/${PROJECT}/features/0`),
		).toBeUndefined();
		expect(
			featureAt(`/devflow/projects/${PROJECT}/features/99999999999`),
		).toBeUndefined();
		expect(featureAt(`/devflow/projects/${PROJECT}/cards/38`)).toBeUndefined();
	});

	test("shows its state, decisions, branch, screenshots and cards", async () => {
		const api = open();

		expect(
			await screen.findByRole("heading", { name: "Feeding plan, version 2" }),
		).toBeTruthy();
		expect(api.loadFeature).toHaveBeenCalledWith(PROJECT, 38);
		expect(
			screen.getByRole("tab", { name: "Projects", selected: true }),
		).toBeTruthy();
		expect(within(await header()).getByText("Building")).toBeTruthy();
		expect(screen.queryByRole("link", { name: /Pull request/ })).toBeNull();

		// The decision, its texts formatted as text, needing me.
		const wants = screen.getByRole("article", {
			name: "What the agents want you to know",
		});
		expect(within(wants).getByText("Needs you")).toBeTruthy();
		const decision = within(
			within(wants).getByRole("list", { name: "Decisions for you" }),
		).getByRole("listitem");
		expect(decision.textContent).toContain("#39 · Plan model and its rules");
		expect(decision.textContent).toContain("Round amounts to 0.5 kg");
		expect(within(decision).getByText("0.5 kg").tagName).toBe("CODE");
		expect(decision.textContent).toContain(
			"Instead of 0.1 kg, as the old plan showed",
		);
		expect(decision.textContent).toContain(
			"Because A scale in a stable is not finer than that",
		);
		expect(decision.textContent).toContain("Reviewer: Agrees.");

		// The branch with the command to check it out.
		const branch = screen.getByRole("region", { name: "Check out the branch" });
		expect(branch.textContent).toContain(checkout(BRANCH));
		expect(checkout(BRANCH)).toBe(
			`git fetch origin ${BRANCH} && git switch ${BRANCH}`,
		);
		expect(screen.getByText("head abc1234")).toBeTruthy();

		// The screenshots by card, read through the card's route.
		const shots = screen.getByRole("region", { name: "Screenshots of #39" });
		const image = within(shots).getAllByRole("img")[0];
		expect(image?.getAttribute("src")).toBe(
			`/devflow/api/projects/${PROJECT}/cards/39/screenshots/plan-desktop`,
		);
		expect(
			screen.queryByRole("region", { name: "Screenshots of #40" }),
		).toBeNull();

		// The cards in order, each title linked to its page.
		const rows = within(
			screen.getByRole("list", { name: "Cards of the feature" }),
		).getAllByRole("listitem");
		expect(rows).toHaveLength(2);
		const link = within(rows[0] as HTMLElement).getByRole("link", {
			name: "Plan model and its rules",
		});
		expect(link.getAttribute("href")).toBe(cardPath(PROJECT, 39));
		expect(rows[0]?.textContent).toContain("#39");
		expect(rows[1]?.textContent).toContain("#40The plan page");
	});

	test("copies the checkout command", async () => {
		const api = open();
		fireEvent.click(
			await screen.findByRole("button", { name: "Copy the command" }),
		);
		await waitFor(() =>
			expect(api.copy).toHaveBeenCalledWith(checkout(BRANCH)),
		);
		expect(await screen.findByRole("button", { name: "Copied" })).toBeTruthy();
	});

	test("answers a decision fine as it is", async () => {
		const api = open();
		fireEvent.click(
			await screen.findByRole("button", { name: /^Fine as it is/ }),
		);

		await waitFor(() =>
			expect(api.answerFine).toHaveBeenCalledWith(PROJECT, 38, RUN, 1),
		);
		const wants = screen.getByRole("article", {
			name: "What the agents want you to know",
		});
		await waitFor(() => expect(within(wants).queryByRole("button")).toBeNull());
		expect(within(wants).getByText("Fine as it is")).toBeTruthy();
		expect(within(wants).queryByText("Needs you")).toBeNull();
	});

	test("an answer that fails says so and reads the page again", async () => {
		const loadFeature = vi.fn(() => Promise.resolve(story()));
		open({
			loadFeature,
			answerFine: () => Promise.reject(new Error("409")),
		});
		fireEvent.click(
			await screen.findByRole("button", { name: /^Fine as it is/ }),
		);

		expect((await screen.findByRole("alert")).textContent).toBe(NOT_ANSWERED);
		expect(loadFeature).toHaveBeenCalledTimes(2);
	});

	test("an answered decision shows its answer and asks nothing", async () => {
		const answered = story();
		const first = answered.cards[0];
		if (first?.decisions[0])
			first.decisions[0].answer = {
				answer: "fine",
				answered_at: "2026-11-20T09:30:00+00:00",
			};
		open({ loadFeature: () => Promise.resolve(answered) });

		const wants = await screen.findByRole("article", {
			name: "What the agents want you to know",
		});
		expect(within(wants).queryByRole("button")).toBeNull();
		expect(within(wants).queryByText("Needs you")).toBeNull();
		expect(within(wants).getByText("Fine as it is")).toBeTruthy();
	});

	test("a feature before its branch, with a pull request, and without decisions", async () => {
		open({
			loadFeature: () =>
				Promise.resolve(
					story({
						branch: null,
						pull_request: {
							number: 52,
							url: "https://github.com/Adamniels/Fodr/pull/52",
						},
						cards: [],
					}),
				),
		});

		expect(
			await screen.findByText("Its branch is made when its first card starts."),
		).toBeTruthy();
		expect(
			screen
				.getByRole("link", { name: "Pull request #52" })
				.getAttribute("href"),
		).toBe("https://github.com/Adamniels/Fodr/pull/52");
		expect(
			screen.queryByRole("article", {
				name: "What the agents want you to know",
			}),
		).toBeNull();
		expect(
			screen.queryByRole("button", { name: "Copy the command" }),
		).toBeNull();
	});

	test("shows its own runs, a conflict resolved among them", async () => {
		open({
			loadFeature: () =>
				Promise.resolve(
					story({
						state: "resolving",
						reason:
							"main conflicts with the feature's branch; an agent resolves it",
						runs: [
							{
								id: "01a3185c-5000-7000-8000-000000000009",
								kind: "conflict",
								worker: "fodr-worker",
								started_at: "2026-11-20T09:00:00+00:00",
								finished_at: "2026-11-20T09:12:00+00:00",
								duration_ms: 720000,
								result: "succeeded",
								reason: null,
								in_card: null,
								auth_method: "oauth_token",
								input_tokens: 12,
								output_tokens: 3400,
								check: { exit_code: 0, output: "Tests: 40 passed" },
								head_commit: COMMIT,
								stderr_tail: null,
							},
							{
								id: "01a3185c-5000-7000-8000-000000000008",
								kind: "conflict",
								worker: "fodr-worker",
								started_at: "2026-11-20T08:40:00+00:00",
								finished_at: "2026-11-20T08:41:00+00:00",
								duration_ms: 60000,
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
					}),
				),
		});

		expect(
			within(await header()).getByText("Resolving a conflict"),
		).toBeTruthy();
		const runs = screen.getByRole("article", { name: "Its own runs" });
		expect(
			within(runs)
				.getAllByRole("term")
				.map((term) => term.textContent),
		).toEqual(["Conflict", "Conflict"]);
		// Each its result and how long it took; a failed one also why, and
		// where its failure lay in a feature's own words.
		expect(
			within(runs)
				.getAllByRole("definition")
				.map((value) => value.textContent),
		).toEqual([
			"Succeeded · 12 min 0 s",
			"Failed · 1 min 0 s",
			"Claude's API did not answer It lay outside the job, so it runs again.",
		]);
	});

	test("shows the review of the whole feature, and whether it covers the head", async () => {
		open({
			loadFeature: () =>
				Promise.resolve(
					story({
						state: "ready",
						pull_request: {
							number: 51,
							url: "https://github.com/Adamniels/Fodr/pull/51",
						},
						reviews: [
							{
								run_id: "019a0000-0000-7000-8000-0000000000f1",
								commit: COMMIT,
								verdict: "approve",
								summary: "The plan and its page fit together.",
								findings: [
									{
										blocking: false,
										file: "src/plan.ts",
										line: 12,
										what: "Two cards round the same amount",
										why: "One rule would do",
									},
								],
								for_owner: [],
								reviewed_at: "2026-11-20T09:00:00+00:00",
								posted: true,
								current: true,
							},
							{
								run_id: "019a0000-0000-7000-8000-0000000000f0",
								commit: "8".repeat(40),
								verdict: "changes",
								summary: "The page reads the plan twice.",
								findings: [],
								for_owner: [],
								reviewed_at: "2026-11-19T09:00:00+00:00",
								posted: true,
								current: false,
							},
						],
					}),
				),
		});

		const reviews = await everyReviewAndRound();
		// The reviews are the list's own items; each review's findings are a list
		// of their own inside it.
		const [latest, older] = Array.from(reviews.children) as [
			HTMLElement,
			HTMLElement,
		];
		expect(within(latest).getByText(COMMIT.slice(0, 7))).toBeTruthy();
		expect(within(latest).getByText("Approved")).toBeTruthy();
		expect(
			within(latest).getByText("For the pull request's head"),
		).toBeTruthy();
		expect(
			within(latest).getByText("The plan and its page fit together."),
		).toBeTruthy();
		expect(
			within(latest).getByText("Two cards round the same amount"),
		).toBeTruthy();
		expect(
			within(older).getByText("For an older commit, so it does not count"),
		).toBeTruthy();
		expect(screen.getByText("Ready for you")).toBeTruthy();
	});

	test("a feature not reviewed yet shows no review", async () => {
		open();

		const panel = await screen.findByRole("article", {
			name: "Reviews and rounds",
		});
		expect(
			within(panel).getByText("The whole feature has not been reviewed yet."),
		).toBeTruthy();
		expect(within(panel).queryByRole("list")).toBeNull();
		expect(within(panel).queryByRole("button")).toBeNull();
	});

	test("a feature with no run of its own shows no runs", async () => {
		open();
		await header();
		expect(screen.queryByRole("article", { name: "Its own runs" })).toBeNull();
	});

	test("a feature the caller does not have is not found", async () => {
		open({ loadFeature: () => Promise.resolve(null) });
		expect(await screen.findByText("There is no such feature")).toBeTruthy();
		expect(
			screen
				.getByRole("link", { name: "Back to the project" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${PROJECT}`);
	});

	test("sends the whole feature back with a comment while it waits for you", async () => {
		const waiting = story({
			state: "waiting_for_owner",
			reason: "Review rounds used up",
			pull_request: {
				number: 51,
				url: "https://github.com/Adamniels/Fodr/pull/51",
			},
		});
		const loadFeature = vi.fn(() => Promise.resolve(waiting));
		const sendBack = vi.fn(() => Promise.resolve());
		open({ loadFeature, sendBack });

		fireEvent.click(
			await screen.findByRole("button", {
				name: "Send the whole feature back",
			}),
		);
		fireEvent.change(
			screen.getByLabelText(
				"What should the developer change in the whole feature?",
			),
			{ target: { value: "Split the list in two." } },
		);
		fireEvent.click(screen.getByRole("button", { name: "Send" }));

		await waitFor(() =>
			expect(sendBack).toHaveBeenCalledWith(
				PROJECT,
				38,
				"Split the list in two.",
			),
		);
		// The page is read again, so it shows the feature being fixed.
		await waitFor(() => expect(loadFeature).toHaveBeenCalledTimes(2));
	});

	test("offers no send back while the feature is still being built", async () => {
		open();

		await screen.findByRole("heading", { name: "Feeding plan, version 2" });
		expect(
			screen.queryByRole("button", { name: "Send the whole feature back" }),
		).toBeNull();
	});

	test("shows each fix round of the whole feature", async () => {
		const asked = "019a0000-0000-7000-8000-0000000000f0";
		open({
			loadFeature: () =>
				Promise.resolve(
					story({
						state: "fixing",
						reason: "Round 1 of 3: the developer is fixing the feature",
						pull_request: {
							number: 51,
							url: "https://github.com/Adamniels/Fodr/pull/51",
						},
						fix_rounds: 1,
						reviews: [
							{
								run_id: asked,
								commit: "8".repeat(40),
								verdict: "changes",
								summary: "The page reads the plan twice.",
								findings: [
									{
										blocking: true,
										file: "src/plan.ts",
										line: 12,
										what: "Two cards read the plan",
										why: "One will drift",
									},
								],
								for_owner: [],
								reviewed_at: "2026-11-20T09:00:00+00:00",
								posted: true,
								current: true,
							},
						],
						rounds: [
							{
								run_id: "019a0000-0000-7000-8000-0000000000f2",
								round: 1,
								review_run_id: asked,
								comments: [],
								result: "succeeded",
								replies: [
									{
										finding: "Two cards read the plan",
										answer: "Fixed: one reader.",
									},
								],
								fixed_commit: COMMIT,
								review_after_run_id: null,
							},
						],
					}),
				),
		});

		const rounds = await everyReviewAndRound();
		expect(screen.getByText("1 of 3 used")).toBeTruthy();
		const round = within(
			within(rounds).getByRole("listitem", { name: "Round 1 of 3" }),
		);
		expect(round.getByText("8888888")).toBeTruthy();
		expect(round.getByText("Fixed: one reader.")).toBeTruthy();
		expect(round.getByText(COMMIT.slice(0, 7))).toBeTruthy();
	});
});

describe("a feature I stopped", () => {
	test("says so, and Run again releases it and reads it again", async () => {
		const stopped = story({
			state: "reviewing",
			reason: "Stopped by you",
			stopped_by_owner: true,
		});
		const feature = open({
			loadFeature: vi
				.fn<FeatureApi["loadFeature"]>()
				.mockResolvedValueOnce(stopped)
				.mockResolvedValue({
					...stopped,
					stopped_by_owner: false,
					reason: null,
				}),
		});

		expect(
			await screen.findByText(
				"Stopped by you. None of its jobs is handed out until you run it again.",
			),
		).toBeTruthy();
		const run = screen.getByRole("button", { name: "Run again" });
		expect(run.className).toMatch(/primary/);
		fireEvent.click(run);

		await waitFor(() =>
			expect(feature.runFeatureAgain).toHaveBeenCalledWith(PROJECT, 38),
		);
		expect(await screen.findByText("Running #38 again")).toBeTruthy();
		await waitFor(() =>
			expect(screen.queryByRole("button", { name: "Run again" })).toBeNull(),
		);
	});

	test("one not stopped by me shows its reason and no Run again", async () => {
		open({
			loadFeature: () =>
				Promise.resolve(
					story({ reason: "Waits for #40, earlier in the feature" }),
				),
		});

		expect(
			await screen.findByText("Waits for #40, earlier in the feature"),
		).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Run again" })).toBeNull();
	});
});

describe("sending a card back", () => {
	afterEach(cleanup);

	test("sends a landed card back with my comment", async () => {
		const loadFeature = vi.fn(() => Promise.resolve(story()));
		const sendBackCard = vi.fn(() => Promise.resolve());
		open({ loadFeature, sendBackCard });

		fireEvent.click(
			await screen.findByRole("button", { name: "Send back #39" }),
		);
		fireEvent.change(
			screen.getByLabelText("What should the developer change in #39?"),
			{ target: { value: "Round to 0.25 kg." } },
		);
		fireEvent.click(screen.getByRole("button", { name: "Send" }));

		await waitFor(() =>
			expect(sendBackCard).toHaveBeenCalledWith(
				PROJECT,
				39,
				"Round to 0.25 kg.",
			),
		);
		// The page is read again, so it shows the card queued.
		await waitFor(() => expect(loadFeature).toHaveBeenCalledTimes(2));
		expect(
			screen.queryByLabelText("What should the developer change in #39?"),
		).toBeNull();
	});

	test("offers it only on a card that can be sent back", async () => {
		open();

		await screen.findByRole("button", { name: "Send back #39" });
		expect(screen.queryByRole("button", { name: "Send back #40" })).toBeNull();
	});

	test("a refused send back says why and keeps my words", async () => {
		open({
			sendBackCard: () =>
				Promise.reject(
					new ApiError({
						status: 409,
						title: "This card cannot be sent back now",
						detail: null,
					}),
				),
		});
		fireEvent.click(
			await screen.findByRole("button", { name: "Send back #39" }),
		);
		const box = screen.getByLabelText(
			"What should the developer change in #39?",
		);
		fireEvent.change(box, { target: { value: "Round to 0.25 kg." } });
		fireEvent.click(screen.getByRole("button", { name: "Send" }));

		expect((await screen.findByRole("alert")).textContent).toBe(
			"This card cannot be sent back now",
		);
		expect(
			(
				screen.getByLabelText(
					"What should the developer change in #39?",
				) as HTMLTextAreaElement
			).value,
		).toBe("Round to 0.25 kg.");
	});

	test("sends a decision's card back with my comment", async () => {
		const sendBackDecision = vi.fn(() => Promise.resolve());
		open({ sendBackDecision });

		const wants = await screen.findByRole("article", {
			name: "What the agents want you to know",
		});
		fireEvent.click(
			within(wants).getByRole("button", { name: /^Send back #39:/ }),
		);
		fireEvent.change(
			within(wants).getByLabelText("What should the developer change in #39?"),
			{ target: { value: "Keep 0.1 kg." } },
		);
		fireEvent.click(within(wants).getByRole("button", { name: "Send" }));

		await waitFor(() =>
			expect(sendBackDecision).toHaveBeenCalledWith(
				PROJECT,
				38,
				RUN,
				1,
				"Keep 0.1 kg.",
			),
		);
	});

	test("a decision of a card not landed is only answered fine", async () => {
		const building = story();
		const first = building.cards[0];
		if (first) first.can_send_back = false;
		open({ loadFeature: () => Promise.resolve(building) });

		const wants = await screen.findByRole("article", {
			name: "What the agents want you to know",
		});
		expect(
			within(wants).getByRole("button", { name: /^Fine as it is/ }),
		).toBeTruthy();
		expect(
			within(wants).queryByRole("button", { name: /^Send back/ }),
		).toBeNull();
	});

	test("a decision sent back says so", async () => {
		const sent = story();
		const first = sent.cards[0];
		if (first?.decisions[0])
			first.decisions[0].answer = {
				answer: "sent_back",
				answered_at: "2026-11-20T09:30:00+00:00",
			};
		open({ loadFeature: () => Promise.resolve(sent) });

		const wants = await screen.findByRole("article", {
			name: "What the agents want you to know",
		});
		expect(within(wants).getByText("Sent back")).toBeTruthy();
		expect(within(wants).queryByRole("button")).toBeNull();
	});

	test("shows a card I sent back with my words", async () => {
		const sent = story();
		const first = sent.cards[0];
		if (first) {
			first.state = "queued";
			first.can_send_back = false;
			first.sent_back = {
				times: 2,
				pull_request: {
					number: 44,
					url: "https://github.com/Adamniels/Fodr/pull/44",
				},
				comments: ["Round to 0.25 kg.\nAnd say so on the page."],
			};
		}
		open({ loadFeature: () => Promise.resolve(sent) });

		const list = await screen.findByRole("list", {
			name: "Cards of the feature",
		});
		const row = within(list).getAllByRole("listitem")[0] as HTMLElement;
		expect(row.textContent).toContain("#39Plan model and its rules");
		expect(row.textContent).toContain(
			"You sent it back 2 times, from pull request #44:",
		);
		expect(within(row).getByText(/^Round to 0.25 kg\./).textContent).toBe(
			"Round to 0.25 kg.\nAnd say so on the page.",
		);
		expect(screen.queryByRole("button", { name: "Send back #39" })).toBeNull();
	});
});

/** What merging the feature confirms, approved with nothing open unless
 * overridden. */
function confirmation(
	overrides: Partial<FeatureMergeConfirmation> = {},
): FeatureMergeConfirmation {
	return {
		number: 38,
		title: "Feeding plan, version 2",
		into: "main",
		pull_request: {
			number: 51,
			url: "https://github.com/Adamniels/Fodr/pull/51",
		},
		head_commit: COMMIT,
		review: "approved",
		open_findings: [],
		unanswered: 0,
		...overrides,
	};
}

/** A feature ready for the owner, offered for merging at COMMIT. */
function ready(): FeatureStory {
	return story({
		state: "ready",
		pull_request: {
			number: 51,
			url: "https://github.com/Adamniels/Fodr/pull/51",
		},
		merge: { into: "main", head_commit: COMMIT },
	});
}

describe("merging the feature", () => {
	test("is not offered when the feature is not offered for merging", async () => {
		open();
		await header();
		expect(screen.queryByRole("button", { name: /^Merge into/ })).toBeNull();
	});

	test("asks first, naming the pull request, its commit and what it confirms", async () => {
		const merged = story({ state: "merged", merge: null });
		const loadFeature = vi
			.fn<FeatureApi["loadFeature"]>()
			.mockResolvedValueOnce(ready())
			.mockResolvedValue(merged);
		const api = open({
			loadFeature,
			loadFeatureMerge: vi.fn(() =>
				Promise.resolve(confirmation({ unanswered: 2 })),
			),
		});

		const feature = within(
			await screen.findByRole("article", { name: "Merging into main" }),
		);
		fireEvent.click(feature.getByRole("button", { name: "Merge into main" }));

		const dialog = within(
			await screen.findByRole("dialog", {
				name: "Merge feature #38 into main?",
			}),
		);
		expect(api.loadFeatureMerge).toHaveBeenCalledWith(PROJECT, 38);
		expect(dialog.getByText(/Pull request #51 at/)).toBeTruthy();
		expect(dialog.getByText("abc1234")).toBeTruthy();
		expect(dialog.getByText("The review approved this commit.")).toBeTruthy();
		expect(
			dialog.getByText("Merging confirms 2 decisions you have not answered."),
		).toBeTruthy();
		expect(dialog.queryByRole("list", { name: "Open findings" })).toBeNull();

		fireEvent.click(dialog.getByRole("button", { name: "Merge" }));

		await waitFor(() =>
			expect(api.mergeFeature).toHaveBeenCalledWith(PROJECT, 38, COMMIT),
		);
		expect(await screen.findByText("Merged feature #38")).toBeTruthy();
		// Read again: merged, and offered no more.
		expect(await screen.findByText("Merged")).toBeTruthy();
		expect(
			screen.queryByRole("button", { name: "Merge into main" }),
		).toBeNull();
	});

	test("says when the review asked for changes, and lists its open findings", async () => {
		open({
			loadFeature: () => Promise.resolve(ready()),
			loadFeatureMerge: () =>
				Promise.resolve(
					confirmation({
						review: "changes",
						open_findings: [
							{
								blocking: true,
								file: "src/plan.ts",
								line: 12,
								what: "Rounds twice",
								why: "The page rounds again",
							},
							{
								blocking: true,
								file: "src/page.tsx",
								line: 0,
								what: "**Shows** the old plan",
								why: "",
							},
						],
						unanswered: 1,
					}),
				),
		});

		fireEvent.click(
			await screen.findByRole("button", { name: "Merge into main" }),
		);
		const dialog = within(await screen.findByRole("dialog"));
		expect(
			dialog.getByText("The review asked for changes: 2 open findings"),
		).toBeTruthy();
		const listed = within(dialog.getByRole("list", { name: "Open findings" }));
		const [first, second] = listed.getAllByRole("listitem");
		expect(listed.getAllByRole("listitem")).toHaveLength(2);
		if (!first || !second) throw new Error("two findings");
		expect(within(first).getByText("src/plan.ts:12")).toBeTruthy();
		expect(within(first).getByText(/The page rounds again/)).toBeTruthy();
		expect(within(second).getByText("src/page.tsx")).toBeTruthy();
		// The reviewer's words are formatted as text, never markup of theirs.
		expect(within(second).getByText("Shows").tagName).toBe("STRONG");
		expect(
			dialog.getByText("Merging confirms 1 decision you have not answered."),
		).toBeTruthy();
	});

	test("says when the feature was not reviewed as a whole", async () => {
		open({
			loadFeature: () => Promise.resolve(ready()),
			loadFeatureMerge: () =>
				Promise.resolve(confirmation({ review: "not_reviewed" })),
		});

		fireEvent.click(
			await screen.findByRole("button", { name: "Merge into main" }),
		);
		const dialog = within(await screen.findByRole("dialog"));
		expect(dialog.getByText("Not reviewed as a whole")).toBeTruthy();
		expect(dialog.queryByText(/Merging confirms/)).toBeNull();
	});

	test("cancelled, nothing is merged", async () => {
		const api = open({
			loadFeature: () => Promise.resolve(ready()),
			loadFeatureMerge: () => Promise.resolve(confirmation()),
		});

		fireEvent.click(
			await screen.findByRole("button", { name: "Merge into main" }),
		);
		const dialog = await screen.findByRole("dialog");
		fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(api.mergeFeature).not.toHaveBeenCalled();
	});

	test("a refused merge is said on the page, and the feature read again", async () => {
		const loadFeature = vi.fn(() => Promise.resolve(ready()));
		open({
			loadFeature,
			loadFeatureMerge: () => Promise.resolve(confirmation()),
			mergeFeature: () =>
				Promise.reject(
					new ApiError({
						status: 409,
						title: "The pull request has changed since this was shown",
						detail: "It was read again. Look at it before you merge.",
					}),
				),
		});

		fireEvent.click(
			await screen.findByRole("button", { name: "Merge into main" }),
		);
		const dialog = within(await screen.findByRole("dialog"));
		fireEvent.click(dialog.getByRole("button", { name: "Merge" }));

		expect((await screen.findByRole("alert")).textContent).toBe(
			"The pull request has changed since this was shown. It was read again. Look at it before you merge.",
		);
		expect(loadFeature).toHaveBeenCalledTimes(2);
	});

	test("cannot be pressed without a usable GitHub link", async () => {
		window.history.pushState(null, "", featurePath(PROJECT, 38));
		render(
			<App
				loadStatus={() =>
					Promise.resolve<Status>({
						code_host: "fake",
						github: { state: "none", login: null },
					})
				}
				waiting={{
					loadWaiting: () => new Promise(() => {}),
					refresh: () => Promise.resolve(),
				}}
				feature={{
					loadFeature: () => Promise.resolve(ready()),
					answerFine: () => new Promise(() => {}),
					loadFeatureMerge: () => Promise.resolve(confirmation()),
					mergeFeature: () => Promise.resolve(),
					sendBack: () => new Promise(() => {}),
					sendBackCard: () => new Promise(() => {}),
					sendBackDecision: () => new Promise(() => {}),
					runFeatureAgain: () => new Promise(() => {}),
				}}
			/>,
		);

		const button = await screen.findByRole("button", {
			name: "Merge into main",
		});
		await waitFor(() => expect(button).toHaveProperty("disabled", true));
		const lineId = button.getAttribute("aria-describedby");
		expect(
			lineId === null ? null : document.getElementById(lineId),
		).toHaveProperty(
			"textContent",
			"Link GitHub on the Projects tab to merge or comment as yourself.",
		);
		// Sending the feature back, beside it, points at the same line rather
		// than saying it twice.
		const sendBack = screen.getByRole("button", {
			name: "Send the whole feature back",
		});
		expect(sendBack).toHaveProperty("disabled", true);
		expect(sendBack.getAttribute("aria-describedby")).toBe(lineId);
	});

	test("a decision confirmed by merging says so", async () => {
		const merged = story({ state: "merged" });
		const decision = merged.cards[0]?.decisions[0];
		if (!decision) throw new Error("a decision");
		decision.answer = {
			answer: "merged",
			answered_at: "2026-11-20T09:30:00+00:00",
		};
		open({ loadFeature: () => Promise.resolve(merged) });

		const decisions = within(
			await screen.findByRole("list", { name: "Decisions for you" }),
		);
		expect(decisions.getByText("Confirmed by merging")).toBeTruthy();
		expect(
			screen.getByText(
				"The feature is merged, and its branch is no longer watched.",
			),
		).toBeTruthy();
	});

	test("a feature closed without merging says so", async () => {
		open({ loadFeature: () => Promise.resolve(story({ state: "closed" })) });

		expect(within(await header()).getByText("Closed")).toBeTruthy();
		expect(
			screen.getByText(
				/Its pull request was closed without merging, so the feature is not in main\./,
			),
		).toBeTruthy();
		expect(screen.queryByRole("button", { name: /^Merge into/ })).toBeNull();
	});
});

type Review = FeatureStory["reviews"][number];
type Run = FeatureStory["runs"][number];

/** A review of the whole feature, approving COMMIT and covering the head
 * unless overridden. */
function review(overrides: Partial<Review> = {}): Review {
	return {
		run_id: "019a0000-0000-7000-8000-0000000000f3",
		commit: COMMIT,
		verdict: "approve",
		summary: "The plan and its page fit together.",
		findings: [],
		for_owner: [],
		reviewed_at: "2026-11-21T09:40:00+00:00",
		posted: true,
		current: true,
		...overrides,
	};
}

/** A run of the feature's own, a review of it that succeeded unless
 * overridden. */
function run(overrides: Partial<Run> = {}): Run {
	return {
		id: "019a0000-0000-7000-8000-0000000000f3",
		kind: "feature_review",
		worker: "fodr-worker",
		started_at: "2026-11-21T09:35:00+00:00",
		finished_at: "2026-11-21T09:40:00+00:00",
		duration_ms: 291000,
		result: "succeeded",
		reason: null,
		in_card: null,
		auth_method: "oauth_token",
		input_tokens: 30,
		output_tokens: 1200,
		check: null,
		head_commit: COMMIT,
		stderr_tail: null,
		...overrides,
	};
}

describe("the page puts merging and the decisions first", () => {
	test("the merge card shows CI, the review and the decisions as tiles with Merge into main as the primary", async () => {
		open({
			loadFeature: () =>
				Promise.resolve({
					...ready(),
					reason: "Every card has landed, and the review approved it.",
					reviews: [review()],
				}),
		});

		// The header: the state and the mono caption, the title as the page
		// title, the reason, and the links.
		const title = await screen.findByRole("heading", {
			level: 2,
			name: "Feeding plan, version 2",
		});
		const header = title.closest("header") as HTMLElement;
		expect(within(header).getByText("Ready for you")).toBeTruthy();
		expect(
			within(header).getByText("ADAMNIELS/FODR · FEATURE #38 · 2 CARDS"),
		).toBeTruthy();
		expect(
			within(header).getByText(
				"Every card has landed, and the review approved it.",
			),
		).toBeTruthy();
		expect(
			within(header)
				.getByRole("link", { name: "Pull request #51" })
				.getAttribute("href"),
		).toBe("https://github.com/Adamniels/Fodr/pull/51");
		expect(
			within(header).getByRole("link", { name: "Issue #38" }),
		).toBeTruthy();

		// The merge card, mine, with the head it merges and three tiles.
		const merging = screen.getByRole("article", { name: "Merging into main" });
		expect(within(merging).getByText("Needs you")).toBeTruthy();
		expect(within(merging).getByText("head abc1234")).toBeTruthy();
		const tiles = within(
			within(merging).getByRole("list", { name: "At a glance" }),
		).getAllByRole("listitem");
		expect(tiles.map((tile) => tile.textContent)).toEqual([
			"CIPassedon abc1234",
			"The review of the whole featureApprovedcovers the head · 0 open",
			"Decisions for you0 of 1 answeredbelow",
		]);

		// Merge into main, the page's one primary, then the whole feature sent
		// back as a secondary, and what merging confirms.
		const merge = within(merging).getByRole("button", {
			name: "Merge into main",
		});
		expect(merge.className).toMatch(/primary/);
		expect(
			screen
				.getAllByRole("button")
				.filter((button) => /primary/.test(button.className)),
		).toEqual([merge]);
		const back = within(merging).getByRole("button", {
			name: "Send the whole feature back",
		});
		expect(back.className).not.toMatch(/primary/);
		expect(merge.compareDocumentPosition(back)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(
			within(merging).getByText(
				"Merging confirms 1 decision you have not answered.",
			),
		).toBeTruthy();

		// The merge card comes before the decisions and the cards.
		const wants = screen.getByRole("article", {
			name: "What the agents want you to know",
		});
		const cards = screen.getByRole("article", { name: "Cards" });
		expect(merging.compareDocumentPosition(wants)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(wants.compareDocumentPosition(cards)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});

	test("without the offer the merge card shows its tiles and no Merge", async () => {
		open();

		const merging = await screen.findByRole("article", {
			name: "Merging into main",
		});
		expect(within(merging).queryByText("Needs you")).toBeNull();
		expect(within(merging).getByText("head abc1234")).toBeTruthy();
		const tiles = within(
			within(merging).getByRole("list", { name: "At a glance" }),
		).getAllByRole("listitem");
		expect(tiles.map((tile) => tile.textContent)).toEqual([
			"CINot passedmerging waits for it",
			"The review of the whole featureNot reviewednot yet",
			"Decisions for you0 of 1 answeredbelow",
		]);
		expect(screen.queryByRole("button", { name: /^Merge into/ })).toBeNull();
		expect(
			screen.queryByRole("button", { name: "Send the whole feature back" }),
		).toBeNull();
		expect(screen.queryByText(/^Merging confirms/)).toBeNull();
	});

	test("an open decision shows its card, the chosen, instead, because and the reviewer, with Fine as it is and Send back; an answered one is one line", async () => {
		const both = story();
		both.cards.push({
			number: 41,
			title: "Merging a plan",
			web_url: "https://github.com/Adamniels/Fodr/issues/41",
			state: "done",
			reason: null,
			fix_rounds: 0,
			input_tokens: 10,
			output_tokens: 900,
			screenshots: null,
			decisions: [
				{
					run_id: "01a3185c-5000-7000-8000-000000000041",
					position: 0,
					chosen: "Refuse the merge when the head moved",
					alternative: "Merge the newer head",
					why: "Nothing unseen is merged",
					reviewer_view: null,
					answer: {
						answer: "fine",
						answered_at: "2026-11-21T10:12:00+00:00",
					},
				},
			],
			can_send_back: true,
			sent_back: null,
		});
		open({ loadFeature: () => Promise.resolve(both) });

		const wants = await screen.findByRole("article", {
			name: "What the agents want you to know",
		});
		expect(within(wants).getByText("Needs you")).toBeTruthy();
		expect(within(wants).getByText("1 open · 1 answered")).toBeTruthy();
		const [waiting, answered] = within(
			within(wants).getByRole("list", { name: "Decisions for you" }),
		).getAllByRole("listitem") as [HTMLElement, HTMLElement];

		// The open one: its card in mono, the chosen, instead of, because and
		// the reviewer's view, then the two answers.
		const card = within(waiting).getByText("#39 · Plan model and its rules");
		expect(card.classList.contains(own.cardLine as string)).toBe(true);
		const chosen = within(waiting).getByText(/^Round amounts to/);
		expect(chosen.classList.contains(own.chosen as string)).toBe(true);
		expect(within(chosen).getByText("0.5 kg").tagName).toBe("CODE");
		expect(waiting.textContent).toContain(
			"Instead of 0.1 kg, as the old plan showed",
		);
		expect(waiting.textContent).toContain(
			"Because A scale in a stable is not finer than that",
		);
		expect(waiting.textContent).toContain("Reviewer: Agrees.");
		const fine = within(waiting).getByRole("button", {
			name: /^Fine as it is/,
		});
		const sendBack = within(waiting).getByRole("button", {
			name: /^Send back #39/,
		});
		expect(sendBack.textContent).toBe("Send back #39");
		expect(fine.className).not.toMatch(/primary/);
		expect(sendBack.className).not.toMatch(/primary/);

		// The answered one: one line, its answer as a status label and when.
		expect(within(answered).queryByRole("button")).toBeNull();
		expect(answered.textContent).toContain("#41 · Merging a plan");
		expect(answered.textContent).toContain(
			"Refuse the merge when the head moved",
		);
		expect(answered.textContent).not.toContain("Instead of");
		expect(within(answered).getByText("Fine as it is")).toBeTruthy();
		expect(within(answered).getByText(/^answered/)).toBeTruthy();
	});

	test("the cards are in order with a steps bar, Landed and Send back on each that can be sent back", async () => {
		const sent = story();
		sent.cards.push({
			number: 42,
			title: "The plan's history",
			web_url: "https://github.com/Adamniels/Fodr/issues/42",
			state: "queued",
			reason: null,
			fix_rounds: 0,
			input_tokens: 0,
			output_tokens: 0,
			screenshots: null,
			decisions: [],
			can_send_back: false,
			sent_back: {
				times: 1,
				pull_request: {
					number: 44,
					url: "https://github.com/Adamniels/Fodr/pull/44",
				},
				comments: ["Keep the history's log on the run."],
			},
		});
		const sendBackCard = vi.fn(() => Promise.resolve());
		open({ loadFeature: () => Promise.resolve(sent), sendBackCard });

		const cards = await screen.findByRole("article", { name: "Cards" });
		expect(within(cards).getByText("1 of 3 landed")).toBeTruthy();
		expect(
			within(cards).getByRole("img", { name: "Cards: 1 of 3 landed" }),
		).toBeTruthy();
		const rows = within(
			within(cards).getByRole("list", { name: "Cards of the feature" }),
		).getAllByRole("listitem") as [HTMLElement, HTMLElement, HTMLElement];
		expect(rows).toHaveLength(3);

		// The landed card: its number, its title linked to its page, its open
		// decision as a badge, Landed, and Send back as a ghost.
		const [landed, building, queued] = rows;
		expect(within(landed).getByText("#39")).toBeTruthy();
		expect(
			within(landed)
				.getByRole("link", { name: "Plan model and its rules" })
				.getAttribute("href"),
		).toBe(cardPath(PROJECT, 39));
		expect(within(landed).getByText("1 decision")).toBeTruthy();
		expect(within(landed).getByText("Landed")).toBeTruthy();
		const sendBack = within(landed).getByRole("button", {
			name: "Send back #39",
		});
		expect(sendBack.textContent).toBe("Send back");
		expect(sendBack.className).toMatch(/ghost/);

		// One still building, and one sent back, showing what I wrote under
		// its row; neither can be sent back.
		expect(within(building).getByText("Building")).toBeTruthy();
		expect(within(building).queryByRole("button")).toBeNull();
		expect(within(queued).queryByRole("button")).toBeNull();
		const said = within(queued).getByText("Keep the history's log on the run.");
		expect(said.classList.contains(own.said as string)).toBe(true);
		expect(queued.textContent).toContain(
			"You sent it back once, from pull request #44:",
		);

		// Send back opens the comment box under its row.
		fireEvent.click(sendBack);
		fireEvent.change(
			within(landed).getByLabelText("What should the developer change in #39?"),
			{ target: { value: "Round to 0.25 kg." } },
		);
		fireEvent.click(within(landed).getByRole("button", { name: "Send" }));
		await waitFor(() =>
			expect(sendBackCard).toHaveBeenCalledWith(
				PROJECT,
				39,
				"Round to 0.25 kg.",
			),
		);

		// The screenshots by card, under the cards.
		const shots = screen.getByRole("article", { name: "Screenshots" });
		expect(within(shots).getByText("1 card with pictures")).toBeTruthy();
		expect(cards.compareDocumentPosition(shots)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});

	test("the side column shows the branch with its command, the fix rounds and the reviews", async () => {
		const asked = "019a0000-0000-7000-8000-0000000000f0";
		const fix = "019a0000-0000-7000-8000-0000000000f2";
		const api = open({
			loadFeature: () =>
				Promise.resolve({
					...ready(),
					fix_rounds: 1,
					reviews: [
						review(),
						review({
							run_id: asked,
							commit: "5".repeat(40),
							verdict: "changes",
							summary: "The page reads the plan twice.",
							findings: [
								{
									blocking: true,
									file: "src/plan.ts",
									line: 12,
									what: "Two cards read the plan",
									why: "One will drift",
								},
							],
							reviewed_at: "2026-11-21T09:05:00+00:00",
							current: false,
						}),
						review({
							run_id: "019a0000-0000-7000-8000-0000000000ef",
							commit: "4".repeat(40),
							verdict: "changes",
							summary: "The first look.",
							reviewed_at: "2026-11-21T08:00:00+00:00",
							current: false,
						}),
					],
					rounds: [
						{
							run_id: fix,
							round: 1,
							review_run_id: asked,
							comments: [],
							result: "succeeded",
							replies: [
								{ finding: "Two cards read the plan", answer: "One reader." },
							],
							fixed_commit: COMMIT,
							review_after_run_id: "019a0000-0000-7000-8000-0000000000f3",
						},
					],
					runs: [
						run(),
						run({
							id: fix,
							kind: "feature_fix",
							started_at: "2026-11-21T09:15:00+00:00",
							duration_ms: 420000,
						}),
						run({
							id: "019a0000-0000-7000-8000-0000000000f9",
							kind: "conflict",
							started_at: "2026-11-21T08:30:00+00:00",
							duration_ms: 124000,
						}),
					],
				}),
		});

		// The branch, when it is watched, and the command to check it out.
		const branch = await screen.findByRole("article", { name: "The branch" });
		expect(within(branch).getByText(BRANCH)).toBeTruthy();
		expect(
			within(branch).getByText(
				"Watched while the feature is open. Check it out to try it yourself:",
			),
		).toBeTruthy();
		expect(
			within(branch).getByRole("region", { name: "Check out the branch" })
				.textContent,
		).toContain(checkout(BRANCH));
		fireEvent.click(
			within(branch).getByRole("button", { name: "Copy the command" }),
		);
		await waitFor(() =>
			expect(api.copy).toHaveBeenCalledWith(checkout(BRANCH)),
		);

		// Reviews and rounds: the fix rounds as steps, and the newest three of
		// the reviews and rounds, newest first.
		const reviews = screen.getByRole("article", { name: "Reviews and rounds" });
		expect(within(reviews).getByText("1 of 3 used")).toBeTruthy();
		expect(
			within(reviews).getByRole("img", { name: "Fix rounds: 1 of 3 used" }),
		).toBeTruthy();
		const latest = within(
			within(reviews).getByRole("list", {
				name: "The latest reviews and rounds",
			}),
		).getAllByRole("listitem");
		expect(latest).toHaveLength(3);
		expect(latest[0]?.textContent).toContain("Review of abc1234");
		expect(latest[0]?.textContent).toContain("Approved");
		expect(latest[0]?.textContent).toContain("covers the head");
		expect(latest[1]?.textContent).toContain("Round 1 of 3");
		expect(latest[1]?.textContent).toContain("Succeeded");
		expect(latest[1]?.textContent).toContain("left abc1234");
		expect(latest[2]?.textContent).toContain("Review of 5555555");
		expect(latest[2]?.textContent).toContain("Changes asked for");
		expect(latest[2]?.textContent).toContain("1 finding, older commit");
		expect(reviews.textContent).not.toContain("4444444");

		// Read every review and round opens them all in place, each in full.
		const every = within(reviews).getByRole("button", {
			name: "Read every review and round",
		});
		expect(every.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(every);
		expect(every.getAttribute("aria-expanded")).toBe("true");
		const all = within(reviews).getByRole("list", {
			name: "Every review and round",
		});
		expect(all.children).toHaveLength(4);
		expect(all.textContent).toContain("4444444");
		expect(all.textContent).toContain("The first look.");
		expect(all.textContent).toContain("One reader.");

		// Its own runs, each a label and a value.
		const runs = screen.getByRole("article", { name: "Its own runs" });
		expect(
			within(runs)
				.getAllByRole("term")
				.map((term) => term.textContent),
		).toEqual(["Review of the feature", "Fix of the feature", "Conflict"]);
		expect(
			within(runs)
				.getAllByRole("definition")
				.map((value) => value.textContent),
		).toEqual([
			"Succeeded · 4 min 51 s",
			"Succeeded · 7 min 0 s",
			"Succeeded · 2 min 4 s",
		]);

		// The side column follows the cards, so it stacks under them on phone.
		const cards = screen.getByRole("article", { name: "Cards" });
		expect(cards.compareDocumentPosition(branch)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});
});
