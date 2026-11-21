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
		title: "Feeding plan, version 2",
		web_url: "https://github.com/Adamniels/Fodr/issues/38",
		state: "building",
		reason: null,
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
		const header = screen.getByRole("article", { name: "The feature" });
		expect(within(header).getByText("Building")).toBeTruthy();
		expect(screen.getByText("1 of 2 cards done")).toBeTruthy();
		expect(screen.queryByRole("link", { name: /Pull request/ })).toBeNull();

		// The decision, its texts formatted as text, needing me.
		const wants = screen.getByRole("article", {
			name: "What the agents want you to know",
		});
		expect(within(wants).getByText("Needs you")).toBeTruthy();
		const decision = within(
			within(wants).getByRole("list", { name: "Decisions for you" }),
		).getByRole("listitem");
		expect(decision.textContent).toContain("#39: Round amounts to 0.5 kg");
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
		expect(screen.getByText("abc1234")).toBeTruthy();

		// The screenshots by card, read through the card's route.
		const shots = screen.getByRole("region", { name: "Screenshots of #39" });
		const image = within(shots).getAllByRole("img")[0];
		expect(image?.getAttribute("src")).toBe(
			`/devflow/api/projects/${PROJECT}/cards/39/screenshots/plan-desktop`,
		);
		expect(
			screen.queryByRole("region", { name: "Screenshots of #40" }),
		).toBeNull();

		// The cards in order, linked to their pages, with rounds and tokens.
		const table = screen.getByRole("table");
		const rows = within(table).getAllByRole("row").slice(1);
		expect(rows).toHaveLength(2);
		const link = within(rows[0] as HTMLElement).getByRole("link", {
			name: "#39 Plan model and its rules",
		});
		expect(link.getAttribute("href")).toBe(cardPath(PROJECT, 39));
		expect(rows[0]?.textContent).toContain("6,154");
		expect(rows[1]?.textContent).toContain("#40 The plan page");
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
							},
						],
					}),
				),
		});

		const feature = await screen.findByRole("article", { name: "The feature" });
		expect(within(feature).getByText("Resolving a conflict")).toBeTruthy();
		const runs = within(
			screen.getByRole("article", { name: "Runs" }),
		).getByRole("list", { name: "Runs of the feature" });
		const items = within(runs).getAllByRole("listitem");
		expect(items).toHaveLength(2);
		const [resolved, failed] = items as [HTMLElement, HTMLElement];
		expect(within(resolved).getByText("Conflict")).toBeTruthy();
		expect(resolved.textContent).toContain(
			`Check exit 0 on ${COMMIT.slice(0, 7)}`,
		);
		expect(resolved.textContent).toContain("Tokens 12 in, 3,400 out");
		// A feature's run says where its failure lay in its own words.
		expect(failed.textContent).toContain(
			"Claude's API did not answer It lay outside the job, so it runs again.",
		);
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

		const reviews = await screen.findByRole("list", {
			name: "Reviews of the feature",
		});
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

		await screen.findByRole("heading", { name: "Feeding plan, version 2" });
		expect(
			screen.queryByRole("list", { name: "Reviews of the feature" }),
		).toBeNull();
	});

	test("a feature with no run of its own shows no runs", async () => {
		open();
		await screen.findByRole("article", { name: "The feature" });
		expect(screen.queryByRole("article", { name: "Runs" })).toBeNull();
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

		const rounds = await screen.findByRole("list", {
			name: "Fix rounds of the feature",
		});
		expect(screen.getByText("Fix rounds, 1 of 3 used")).toBeTruthy();
		const round = within(
			within(rounds).getByRole("listitem", { name: "Round 1 of 3" }),
		);
		expect(round.getByText("8888888")).toBeTruthy();
		expect(round.getByText("Fixed: one reader.")).toBeTruthy();
		expect(round.getByText(COMMIT.slice(0, 7))).toBeTruthy();
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
			within(wants).getByRole("button", { name: /^Send back: #39/ }),
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
			name: "Cards you sent back",
		});
		expect(list.textContent).toContain("#39 Plan model and its rules");
		expect(list.textContent).toContain(
			"Sent back from pull request #44, 2 times in all",
		);
		expect(list.textContent).toContain(
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
		await screen.findByRole("article", { name: "The feature" });
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
			await screen.findByRole("article", { name: "The feature" }),
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

		const feature = within(
			await screen.findByRole("article", { name: "The feature" }),
		);
		expect(feature.getByText("Closed")).toBeTruthy();
		expect(
			screen.getByText(
				/Its pull request was closed without merging, so the feature is not in main\./,
			),
		).toBeTruthy();
		expect(screen.queryByRole("button", { name: /^Merge into/ })).toBeNull();
	});
});
