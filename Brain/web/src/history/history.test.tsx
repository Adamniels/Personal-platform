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

function openHistory(brain: ReturnType<typeof fakeBrain>) {
	window.history.pushState(null, "", "/brain/history");
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
}

async function timeline() {
	const panel = await screen.findByRole("tabpanel", { name: "History" });
	return within(await within(panel).findByRole("list", { name: "History" }));
}

/** Each entry's when and its line, in the order shown. */
async function shown(): Promise<string[]> {
	const list = await timeline();
	return list
		.getAllByRole("listitem")
		.map(
			(entry) =>
				`${entry.querySelector("span[class*='time']")?.textContent} ${entry.querySelector("p")?.textContent}`,
		);
}

describe("history is newest first by when it happened", () => {
	test("not by when it was entered, each written to its precision", async () => {
		const brain = fakeBrain();
		brain.happened("Started the MSc", "2025-09-01T00:00:00Z", "month");
		brain.happened("Took the course", "2021-01-01T00:00:00Z", "year");
		brain.happened("Shipped the brain", "2026-11-12T00:00:00Z", "day");
		openHistory(brain);

		await waitFor(async () =>
			expect(await shown()).toEqual([
				"12 Nov 2026 Shipped the brain",
				"Sep 2025 Started the MSc",
				"2021 Took the course",
			]),
		);
	});

	test("a decision is marked, with its reasoning, and each entry its subjects", async () => {
		const brain = fakeBrain();
		brain.topic("Rust");
		brain.decided(
			"The brain is its own service",
			{ reasoning: "Splitting memory across languages failed." },
			{ occurredAt: "2026-07-31T00:00:00Z", precision: "day" },
		);
		brain.happened("Read the book", "2026-03-01T00:00:00Z", "month", {
			topics: ["rust"],
			entities: ["The Rust Book"],
		});
		openHistory(brain);

		const list = await timeline();
		const [decision, read] = list.getAllByRole("listitem") as [
			HTMLElement,
			HTMLElement,
		];
		expect(within(decision).getByText("Decision")).toBeTruthy();
		expect(
			within(decision).getByText("Splitting memory across languages failed."),
		).toBeTruthy();
		expect(within(read).queryByText("Decision")).toBeNull();
		expect(within(read).getByText("Rust")).toBeTruthy();
		expect(within(read).getByText("The Rust Book")).toBeTruthy();
	});

	test("offers no primary button, and says when there is nothing", async () => {
		const brain = fakeBrain();
		openHistory(brain);

		const panel = within(
			await screen.findByRole("tabpanel", { name: "History" }),
		);
		expect(await panel.findByText("Nothing in your history yet")).toBeTruthy();
		const buttons = panel
			.getAllByRole("button")
			.map((button) => button.textContent);
		expect(buttons).toEqual(["Add what happened"]);
	});
});

describe("the filter row narrows by topic and by named thing", () => {
	test("each select sends its value, and Any sends none", async () => {
		const brain = fakeBrain();
		brain.topic("Rust");
		brain.entity("Personal platform");
		brain.happened("Read the book", "2026-03-01T00:00:00Z", "month", {
			topics: ["rust"],
		});
		brain.happened("Started the platform", "2026-01-01T00:00:00Z", "month", {
			entities: ["Personal platform"],
		});
		openHistory(brain);
		await timeline();

		const filters = within(
			screen.getByRole("search", { name: "Filter history" }),
		);
		const topic = filters.getByLabelText("Topic") as HTMLSelectElement;
		await waitFor(() =>
			expect([...topic.options].map((option) => option.text)).toEqual([
				"Any",
				"Rust",
			]),
		);
		fireEvent.change(topic, { target: { value: "rust" } });
		await waitFor(async () =>
			expect(await shown()).toEqual(["Mar 2026 Read the book"]),
		);

		fireEvent.change(topic, { target: { value: "" } });
		const entity = filters.getByLabelText("Named thing") as HTMLSelectElement;
		await waitFor(() => expect(entity.options).toHaveLength(2));
		fireEvent.change(entity, { target: { value: "Personal platform" } });
		await waitFor(async () =>
			expect(await shown()).toEqual(["Jan 2026 Started the platform"]),
		);
		const asked = brain.calls
			.filter(({ path }) => path.startsWith("/brain/api/history"))
			.map(({ path }) => path);
		expect(asked).toContain("/brain/api/history?topic=rust");
		expect(asked).toContain("/brain/api/history?entity=Personal+platform");
	});
});

describe("adding what happened", () => {
	async function openForm() {
		const panel = within(
			await screen.findByRole("tabpanel", { name: "History" }),
		);
		fireEvent.click(
			await panel.findByRole("button", { name: "Add what happened" }),
		);
		return within(panel.getByRole("form", { name: "Add what happened" }));
	}

	test("a year alone is sent as that year", async () => {
		const brain = fakeBrain();
		openHistory(brain);

		const form = await openForm();
		fireEvent.change(form.getByLabelText("What happened"), {
			target: { value: "Took the distributed systems course" },
		});
		fireEvent.change(form.getByLabelText("Precision"), {
			target: { value: "year" },
		});
		fireEvent.change(form.getByLabelText("Year"), {
			target: { value: "2021" },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		await waitFor(async () =>
			expect(await shown()).toEqual([
				"2021 Took the distributed systems course",
			]),
		);
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: "/brain/api/items",
				body: {
					kind: "history",
					content: "Took the distributed systems course",
					occurred_at: "2021-01-01T00:00:00Z",
					occurred_precision: "year",
					topics: [],
					entities: [],
				},
			},
		]);
	});

	test("a month is chosen by name, beside its year", async () => {
		const brain = fakeBrain();
		openHistory(brain);

		const form = await openForm();
		fireEvent.change(form.getByLabelText("What happened"), {
			target: { value: "Started the MSc" },
		});
		fireEvent.change(form.getByLabelText("Precision"), {
			target: { value: "month" },
		});
		fireEvent.change(form.getByLabelText("Month"), {
			target: { value: "9" },
		});
		fireEvent.change(form.getByLabelText("Year"), {
			target: { value: "2025" },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		await waitFor(async () =>
			expect(await shown()).toEqual(["Sep 2025 Started the MSc"]),
		);
		expect(brain.changes()[0]?.body).toMatchObject({
			occurred_at: "2025-09-01T00:00:00Z",
			occurred_precision: "month",
		});
	});

	test("it needs to say when, and sends nothing until it does", async () => {
		const brain = fakeBrain();
		openHistory(brain);

		const form = await openForm();
		fireEvent.change(form.getByLabelText("What happened"), {
			target: { value: "Something, sometime" },
		});
		// A day is asked for at first, empty, since only the owner knows it.
		expect((form.getByLabelText("Day") as HTMLInputElement).value).toBe("");
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		expect(form.getByText("Error: Say when it happened")).toBeTruthy();
		expect(brain.changes()).toEqual([]);
	});
});
