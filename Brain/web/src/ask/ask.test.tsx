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
import { App } from "../app";
import { Providers } from "../providers";
import { fakeBrain } from "../test/fake-brain";

afterEach(cleanup);

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
});
