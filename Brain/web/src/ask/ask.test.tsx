import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { createClient } from "../api/client";
import type { components } from "../api/schema";
import { App } from "../app";
import { Providers } from "../providers";
import { answerItem, fakeBrain } from "../test/fake-brain";

type TopicAnswer = components["schemas"]["TopicAnswer"];

afterEach(cleanup);

// This year, so a support line reads without one whenever the tests run.
const YEAR = new Date().getFullYear();

const QUESTIONS = [
	"Who is this person?",
	"What do they know?",
	"What is the history?",
	"What is going on now?",
];

function openAsk(brain: ReturnType<typeof fakeBrain>) {
	window.history.pushState(null, "", "/brain/ask");
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
}

async function ask(): Promise<HTMLElement> {
	return screen.findByRole("tabpanel", { name: "Ask" });
}

async function choose(question: string) {
	const panel = within(await ask());
	fireEvent.click(panel.getByRole("tab", { name: question }));
	return within(await screen.findByRole("tabpanel", { name: question }));
}

function press(scope: ReturnType<typeof within>, name: string) {
	fireEvent.click(scope.getByRole("button", { name }));
}

/** The card an item's text is in. */
async function card(text: string): Promise<HTMLElement> {
	const panel = within(await ask());
	const found = await panel.findByText(text, { selector: "p" });
	const item = found.closest("li");
	if (!item) throw new Error(`${text} is not in a list item`);
	return item;
}

function topicAnswer(overrides: Partial<TopicAnswer>): TopicAnswer {
	return {
		asked: "rust",
		topic: { id: crypto.randomUUID(), slug: "rust", name: "Rust" },
		known: true,
		level: null,
		focus: null,
		misconceptions: [],
		history: [],
		narrower: [],
		related: [],
		...overrides,
	};
}

describe("the form asks only for what its question needs", () => {
	test("the four questions, Who is this person? first, and Nothing asked yet", async () => {
		openAsk(fakeBrain());
		const panel = within(await ask());

		expect(
			panel.getByText(
				"Ask what a feature would ask, and see exactly what it gets.",
			),
		).toBeTruthy();
		const tabs = panel.getByRole("tablist", { name: "Questions" });
		expect(
			within(tabs)
				.getAllByRole("tab")
				.map((tab) => tab.textContent),
		).toEqual(QUESTIONS);
		expect(
			within(tabs).getByRole("tab", { name: QUESTIONS[0] }).ariaSelected,
		).toBe("true");
		expect(panel.getByText("Nothing asked yet")).toBeTruthy();
	});

	test("every question asks about now or history, now unless changed", async () => {
		openAsk(fakeBrain());
		for (const question of QUESTIONS) {
			const form = await choose(question);
			const about = form.getByLabelText("About") as HTMLSelectElement;
			expect(about.value).toBe("now");
			expect([...about.options].map((option) => option.text)).toEqual([
				"Now",
				"History",
			]);
			expect(form.getAllByRole("button", { name: "Ask" })).toHaveLength(1);
		}
	});

	test("who is this person and what is going on now ask for nothing else", async () => {
		openAsk(fakeBrain());
		for (const question of [QUESTIONS[0], QUESTIONS[3]] as string[]) {
			const form = await choose(question);
			expect(form.getAllByRole("combobox")).toHaveLength(1);
			expect(form.queryByRole("textbox")).toBeNull();
		}
	});

	test("what do they know asks for topics, one at a time, as removable pills", async () => {
		const brain = fakeBrain();
		brain.topic("Rust");
		brain.topic("Postgres");
		const provisional = brain.topic("Kafka", { state: "provisional" });
		brain.answers("/ask/topics", { mode: "now", topics: [], conflicts: [] });
		openAsk(brain);

		const form = await choose("What do they know?");
		const pick = form.getByLabelText("Topic") as HTMLSelectElement;
		await waitFor(() => expect(pick.options.length).toBe(3));
		// Only the canonical ones, by name.
		expect([...pick.options].map((option) => option.text)).toEqual([
			"Choose a topic",
			"Postgres",
			"Rust",
		]);
		expect(
			[...pick.options].some((option) => option.value === provisional.slug),
		).toBe(false);

		fireEvent.change(pick, { target: { value: "rust" } });
		fireEvent.change(pick, { target: { value: "postgres" } });

		// Chosen ones leave the select, which goes back to asking.
		expect(pick.value).toBe("");
		expect([...pick.options].map((option) => option.text)).toEqual([
			"Choose a topic",
		]);
		expect(
			form.getByRole("button", { name: "Remove filter Rust" }),
		).toBeTruthy();
		press(form, "Remove filter Rust");
		expect(
			form.queryByRole("button", { name: "Remove filter Rust" }),
		).toBeNull();
		fireEvent.change(pick, { target: { value: "rust" } });

		press(form, "Ask");
		await waitFor(() =>
			expect(brain.asked()).toEqual([
				{
					method: "POST",
					path: "/brain/api/ask/topics",
					body: { mode: "now", topics: ["postgres", "rust"] },
				},
			]),
		);
	});

	test("what do they know refuses to ask about no topic", async () => {
		const brain = fakeBrain();
		brain.topic("Rust");
		openAsk(brain);

		const form = await choose("What do they know?");
		press(form, "Ask");

		expect(await form.findByText("Error: Choose a topic first")).toBeTruthy();
		expect(brain.asked()).toEqual([]);
	});

	test("what is the history asks for one topic or one named thing", async () => {
		const brain = fakeBrain();
		brain.topic("Rust");
		brain.entity("Operation Rollout");
		brain.answers("/ask/history", (body: unknown) => ({
			asked: (body as { entity: string }).entity,
			known: true,
			topic: null,
			entity: { id: crypto.randomUUID(), name: "Operation Rollout" },
			items: [],
			narrower: [],
			related: [],
			conflicts: [],
			mode: "history",
		}));
		openAsk(brain);

		const form = await choose("What is the history?");
		const subject = form.getByLabelText("Subject") as HTMLSelectElement;
		await waitFor(() => expect(subject.options.length).toBe(3));
		expect([...subject.options].map((option) => option.text)).toEqual([
			"Choose one",
			"Rust",
			"Operation Rollout",
		]);
		press(form, "Ask");
		expect(
			await form.findByText("Error: Choose a topic or a named thing first"),
		).toBeTruthy();

		fireEvent.change(subject, {
			target: { value: "entity:Operation Rollout" },
		});
		fireEvent.change(form.getByLabelText("About"), {
			target: { value: "history" },
		});
		press(form, "Ask");

		await waitFor(() =>
			expect(brain.asked()).toEqual([
				{
					method: "POST",
					path: "/brain/api/ask/history",
					body: { mode: "history", entity: "Operation Rollout" },
				},
			]),
		);
	});

	test("nothing is sent until Ask, and changing the question or the mode clears the answer", async () => {
		const brain = fakeBrain();
		brain.answers("/ask/now", {
			mode: "now",
			items: [answerItem({ content: "Writing the Ask section" })],
			conflicts: [],
		});
		openAsk(brain);

		let form = await choose("What is going on now?");
		expect(brain.asked()).toEqual([]);
		press(form, "Ask");
		await card("Writing the Ask section");
		expect(brain.asked()).toHaveLength(1);

		fireEvent.change(form.getByLabelText("About"), {
			target: { value: "history" },
		});
		expect(form.queryByText("Writing the Ask section")).toBeNull();
		expect(form.getByText("Nothing asked yet")).toBeTruthy();

		press(form, "Ask");
		await card("Writing the Ask section");
		form = await choose("Who is this person?");
		expect(screen.queryByText("Writing the Ask section")).toBeNull();
		expect(form.getByText("Nothing asked yet")).toBeTruthy();
		expect(brain.asked()).toHaveLength(2);
	});
});

describe("an answer shows each item with its reasons", () => {
	test("where it lives, where it came from, how much supports it, and when", async () => {
		const brain = fakeBrain();
		const rust = { id: crypto.randomUUID(), slug: "rust", name: "Rust" };
		brain.answers("/ask/now", {
			mode: "now",
			items: [
				answerItem({
					content: "Learning Rust ownership",
					subjects: { topics: [rust], entities: [] },
					last_supported_at: `${YEAR}-11-12T10:00:00Z`,
					support: {
						supports: {
							activity: 1,
							assertion: 1,
							decision: 0,
							engagement: 0,
							exposure: 0,
							judgment: 0,
						},
						contradicts: {
							activity: 0,
							assertion: 0,
							decision: 0,
							engagement: 0,
							exposure: 0,
							judgment: 0,
						},
					},
					evidence: [
						{
							verb: "asserted",
							kind: "supports",
							text: "Entered by hand as state: Learning Rust ownership",
							origin: "brain",
							occurred_at: `${YEAR}-11-01T10:00:00Z`,
							occurred_precision: "instant",
							recorded_at: `${YEAR}-11-01T10:00:00Z`,
						},
						{
							verb: "practised",
							kind: "supports",
							text: "Did the borrow checker exercises",
							origin: "learning",
							occurred_at: `${YEAR}-11-12T10:00:00Z`,
							occurred_precision: "instant",
							recorded_at: `${YEAR}-11-12T10:00:00Z`,
						},
					],
				}),
				answerItem({
					content: "Finishing the thesis draft",
					stale: true,
					last_supported_at: `${YEAR}-08-01T10:00:00Z`,
				}),
			],
			conflicts: [],
		});
		openAsk(brain);

		const form = await choose("What is going on now?");
		press(form, "Ask");

		const item = within(await card("Learning Rust ownership"));
		const facts = within(item.getByRole("list", { name: "Facts" }));
		expect(
			facts.getAllByRole("listitem").map((fact) => fact.textContent),
		).toEqual([
			"Now",
			"Semantic store",
			"Entered by hand",
			"Supported by 2 entries, last on 12 Nov",
		]);
		expect(item.getByText("Rust")).toBeTruthy();
		expect(item.getByText("Active")).toBeTruthy();
		// No score: the brain returns none.
		expect(item.queryByText(/score|relevance/i)).toBeNull();

		// Why is closed until opened, the events oldest first.
		const why = item.getByRole("button", { name: "Why" });
		expect(why.getAttribute("aria-expanded")).toBe("false");
		expect(item.queryByRole("list", { name: "Why" })).toBeNull();
		fireEvent.click(why);
		expect(why.getAttribute("aria-expanded")).toBe("true");
		const reasons = within(item.getByRole("list", { name: "Why" }));
		const lines = reasons.getAllByRole("listitem");
		expect(lines).toHaveLength(2);
		expect(lines[0]?.textContent).toContain("You entered this");
		expect(lines[1]?.textContent).toContain("Practised in learning");
		expect(lines[1]?.textContent).toContain("Did the borrow checker exercises");

		const stale = within(await card("Finishing the thesis draft"));
		expect(stale.getByText("Still true?")).toBeTruthy();
		expect(stale.getByText("Supported by 1 entry, last on 1 Aug")).toBeTruthy();
	});

	test("the items keep the order the brain returned them in", async () => {
		const brain = fakeBrain();
		brain.answers("/ask/now", {
			mode: "now",
			items: [
				answerItem({
					content: "Older first",
					last_supported_at: "2026-01-01T00:00:00Z",
				}),
				answerItem({
					content: "Newer second",
					last_supported_at: "2026-11-01T00:00:00Z",
				}),
			],
			conflicts: [],
		});
		openAsk(brain);

		const form = await choose("What is going on now?");
		press(form, "Ask");

		await card("Older first");
		const list = form.getByRole("list", { name: "Answer" });
		expect(
			[...list.children].map((item) => item.querySelector("p")?.textContent),
		).toEqual(["Older first", "Newer second"]);
	});

	test("what do they know groups per topic, says what answered, and the narrower ones through which", async () => {
		const brain = fakeBrain();
		brain.topic("Postgres");
		const indexing = {
			id: crypto.randomUUID(),
			slug: "postgres-indexing",
			name: "Postgres indexing",
		};
		brain.answers("/ask/topics", {
			mode: "now",
			conflicts: [],
			topics: [
				topicAnswer({
					asked: "postgres",
					topic: {
						id: crypto.randomUUID(),
						slug: "postgres",
						name: "Postgres",
					},
					level: answerItem({
						kind: "topic_level",
						store: "knowledge",
						content: "Level for Postgres: solid",
					}),
					related: ["sqlite"],
					narrower: [
						{
							topic: indexing,
							via: ["postgres"],
							level: null,
							focus: null,
							misconceptions: [],
							history: [
								answerItem({
									kind: "history",
									store: "reported_history",
									nature: "record",
									content: "Read about B-tree indexes",
								}),
							],
						},
					],
				}),
				topicAnswer({
					asked: "postgres-indexes",
					topic: indexing,
				}),
			],
		});
		openAsk(brain);

		const form = await choose("What do they know?");
		await waitFor(() =>
			expect(
				(form.getByLabelText("Topic") as HTMLSelectElement).options.length,
			).toBe(2),
		);
		fireEvent.change(form.getByLabelText("Topic"), {
			target: { value: "postgres" },
		});
		press(form, "Ask");

		await card("Level for Postgres: solid");
		const headings = form
			.getAllByRole("heading")
			.map((heading) => heading.textContent);
		expect(headings).toEqual([
			"postgres",
			"postgres-indexing",
			"postgres-indexes, answered as postgres-indexing",
		]);
		expect(form.getByText("Through postgres")).toBeTruthy();
		expect(form.getByText("Related: sqlite")).toBeTruthy();
		const narrower = within(
			form.getByRole("list", { name: "postgres-indexing" }),
		);
		expect(narrower.getByText("Read about B-tree indexes")).toBeTruthy();
	});
});

describe("a conflict names both items, which is preferred and why, or that neither is", () => {
	const global = answerItem({
		kind: "decision",
		content: "Use Postgres for everything",
	});
	const narrow = answerItem({
		kind: "decision",
		content: "Use SQLite for the CLI tool",
	});
	const newer = answerItem({ kind: "decision", content: "Ship weekly" });
	const older = answerItem({ kind: "decision", content: "Ship monthly" });
	const topic = answerItem({ kind: "decision", content: "Index every key" });
	const entity = answerItem({
		kind: "decision",
		content: "No indexes in Rollout",
	});

	test("narrower scope, newer at equal scope, and no winner", async () => {
		const brain = fakeBrain();
		brain.answers("/ask/now", {
			mode: "now",
			items: [narrow, global, newer, older, topic, entity],
			conflicts: [
				{
					items: [global.id, narrow.id],
					prefer: narrow.id,
					reason: "narrower scope",
				},
				{
					items: [older.id, newer.id],
					prefer: newer.id,
					reason: "newer at equal scope",
				},
				{
					items: [topic.id, entity.id],
					prefer: null,
					reason: "incomparable scopes",
				},
			],
		});
		openAsk(brain);

		const form = await choose("What is going on now?");
		press(form, "Ask");

		const box = within(
			await form.findByRole("article", {
				name: "The brain holds things that disagree",
			}),
		);
		const conflicts = box.getAllByRole("listitem");
		expect(conflicts).toHaveLength(3);

		const [first, second, third] = conflicts.map((conflict) =>
			within(conflict),
		);
		expect(first?.getAllByRole("link").map((link) => link.textContent)).toEqual(
			["Use Postgres for everything", "Use SQLite for the CLI tool"],
		);
		expect(first?.getByText("Narrower scope")).toBeTruthy();
		expect(
			first?.getByText("Preferred: Use SQLite for the CLI tool"),
		).toBeTruthy();
		expect(second?.getByText("Newer at equal scope")).toBeTruthy();
		expect(second?.getByText("Preferred: Ship weekly")).toBeTruthy();
		expect(third?.getByText("No winner: unrelated subjects")).toBeTruthy();
		expect(third?.queryByText(/^Preferred/)).toBeNull();

		// Each link goes down to its item, and both items stay in the list.
		const link = first?.getByRole("link", {
			name: "Use SQLite for the CLI tool",
		});
		const target = link?.getAttribute("href")?.slice(1) ?? "";
		const item = document.getElementById(target);
		expect(item?.textContent).toContain("Use SQLite for the CLI tool");
		expect(await card("Use Postgres for everything")).toBeTruthy();
		expect(await card("Use SQLite for the CLI tool")).toBeTruthy();
	});

	test("an answer without conflicts has no conflicts card", async () => {
		const brain = fakeBrain();
		brain.answers("/ask/now", { mode: "now", items: [global], conflicts: [] });
		openAsk(brain);

		const form = await choose("What is going on now?");
		press(form, "Ask");

		await card("Use Postgres for everything");
		expect(
			form.queryByRole("article", {
				name: "The brain holds things that disagree",
			}),
		).toBeNull();
	});
});

describe("a topic the brain does not know is said plainly", () => {
	test("Nothing known about <topic>", async () => {
		const brain = fakeBrain();
		brain.topic("Haskell");
		brain.answers("/ask/topics", {
			mode: "now",
			conflicts: [],
			topics: [topicAnswer({ asked: "haskell", topic: null, known: false })],
		});
		openAsk(brain);

		const form = await choose("What do they know?");
		await waitFor(() =>
			expect(
				(form.getByLabelText("Topic") as HTMLSelectElement).options.length,
			).toBe(2),
		);
		fireEvent.change(form.getByLabelText("Topic"), {
			target: { value: "haskell" },
		});
		press(form, "Ask");

		expect(await form.findByText("Nothing known about haskell")).toBeTruthy();
	});
});

describe("the raw answer", () => {
	test("Show what a feature receives holds the answer as JSON, with Copy", async () => {
		const brain = fakeBrain();
		const answer = {
			mode: "now",
			items: [answerItem({ content: "Writing the Ask section" })],
			conflicts: [],
		};
		brain.answers("/ask/now", answer);
		let copied = "";
		Object.defineProperty(navigator, "clipboard", {
			configurable: true,
			value: {
				writeText: (text: string) => {
					copied = text;
					return Promise.resolve();
				},
			},
		});
		openAsk(brain);

		const form = await choose("What is going on now?");
		press(form, "Ask");
		await card("Writing the Ask section");

		const show = form.getByRole("button", {
			name: "Show what a feature receives",
		});
		expect(show.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(show);
		const raw = form.getByLabelText("What a feature receives");
		expect(JSON.parse(raw.textContent ?? "")).toEqual(answer);

		press(form, "Copy");
		await waitFor(() => expect(JSON.parse(copied)).toEqual(answer));
		expect(await screen.findByText("Answer copied")).toBeTruthy();
	});
});
