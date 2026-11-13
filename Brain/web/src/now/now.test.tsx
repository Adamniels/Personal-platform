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
import { confirmedLabel } from "./confirmed";

afterEach(cleanup);

const DAY = 24 * 60 * 60 * 1000;

function daysAgo(days: number): string {
	return new Date(Date.now() - days * DAY).toISOString();
}

function openNow(brain: ReturnType<typeof fakeBrain>) {
	window.history.pushState(null, "", "/brain/now");
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
}

async function entry(text: string): Promise<HTMLElement> {
	const panel = await screen.findByRole("tabpanel", { name: "Now" });
	const found = await within(panel).findByText(text);
	const row = found.closest("li");
	if (!row) throw new Error(`${text} is not in a list item`);
	return row;
}

describe("a stale entry is marked needs you, and a fresh one is not marked", () => {
	test("only the stale one says Still true? and offers Still true", async () => {
		const brain = fakeBrain();
		brain.state("Finishing the thesis draft", { supportedAt: daysAgo(90) });
		brain.state("Learning Rust", { supportedAt: daysAgo(2) });
		openNow(brain);

		const stale = within(await entry("Finishing the thesis draft"));
		const mark = stale.getByText("Still true?");
		expect(mark.closest("[class*='needs-you']")).not.toBeNull();
		expect(stale.getByRole("button", { name: "Still true" })).toBeTruthy();

		const fresh = within(await entry("Learning Rust"));
		expect(fresh.queryByText("Still true?")).toBeNull();
		expect(fresh.queryByRole("button", { name: "Still true" })).toBeNull();
		expect(fresh.getByText(/^Confirmed /)).toBeTruthy();
	});

	test("Still true confirms it, and the mark goes", async () => {
		const brain = fakeBrain();
		const old = brain.state("Finishing the thesis draft", {
			supportedAt: daysAgo(90),
		});
		openNow(brain);

		const row = within(await entry("Finishing the thesis draft"));
		fireEvent.click(row.getByRole("button", { name: "Still true" }));

		await waitFor(() => expect(row.queryByText("Still true?")).toBeNull());
		expect(row.getByText("Confirmed today")).toBeTruthy();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: `/brain/api/items/${old.id}/confirm`,
				body: undefined,
			},
		]);
		// The button it pressed is gone, so focus moves to the entry's Edit.
		await waitFor(() =>
			expect(document.activeElement).toBe(
				row.getByRole("button", { name: "Edit" }),
			),
		);
	});
});

describe("the list", () => {
	test("newest support first, each with its subjects", async () => {
		const brain = fakeBrain();
		brain.topic("Rust");
		brain.state("Older", { supportedAt: daysAgo(10) });
		brain.state("Newest", {
			supportedAt: daysAgo(1),
			topics: ["rust"],
			entities: ["Personal platform"],
		});
		openNow(brain);

		await entry("Newest");
		const panel = screen.getByRole("tabpanel", { name: "Now" });
		const rows = within(panel).getAllByRole("listitem");
		expect(rows.map((row) => row.querySelector("p")?.textContent)).toEqual([
			"Newest",
			"Older",
		]);
		const newest = within(rows[0] as HTMLElement);
		expect(newest.getByText("Rust")).toBeTruthy();
		expect(newest.getByText("Personal platform")).toBeTruthy();
	});

	test("says what belongs here, and when there is nothing yet", async () => {
		const brain = fakeBrain();
		openNow(brain);

		const panel = within(await screen.findByRole("tabpanel", { name: "Now" }));
		expect(
			await panel.findByText(
				"What you are working on, what is blocking you, what is coming up. The brain asks again after sixty days.",
			),
		).toBeTruthy();
		expect(panel.getByText("Nothing going on yet")).toBeTruthy();
	});
});

describe("adding", () => {
	test("takes the text, and a topic and a named thing when given", async () => {
		const brain = fakeBrain();
		brain.topic("Rust");
		brain.topic("Gardening", { state: "provisional" });
		brain.entity("Personal platform");
		openNow(brain);

		const panel = within(await screen.findByRole("tabpanel", { name: "Now" }));
		fireEvent.click(await panel.findByRole("button", { name: "Add" }));
		const form = within(panel.getByRole("form", { name: "Add" }));
		// Only canonical topics are offered.
		const topic = form.getByLabelText("Topic") as HTMLSelectElement;
		await waitFor(() =>
			expect([...topic.options].map((option) => option.text)).toEqual([
				"None",
				"Rust",
			]),
		);
		fireEvent.change(form.getByLabelText("What is going on"), {
			target: { value: "Writing the brain's memory centre" },
		});
		fireEvent.change(topic, { target: { value: "rust" } });
		fireEvent.change(form.getByLabelText("Named thing"), {
			target: { value: "  Personal platform " },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		const row = within(await entry("Writing the brain's memory centre"));
		expect(row.getByText("Confirmed today")).toBeTruthy();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: "/brain/api/items",
				body: {
					kind: "state",
					content: "Writing the brain's memory centre",
					topics: ["rust"],
					entities: ["Personal platform"],
				},
			},
		]);
	});

	test("without subjects it names none", async () => {
		const brain = fakeBrain();
		openNow(brain);

		const panel = within(await screen.findByRole("tabpanel", { name: "Now" }));
		fireEvent.click(await panel.findByRole("button", { name: "Add" }));
		const form = within(panel.getByRole("form", { name: "Add" }));
		fireEvent.change(form.getByLabelText("What is going on"), {
			target: { value: "Exams next week" },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		await entry("Exams next week");
		expect(brain.changes()[0]?.body).toEqual({
			kind: "state",
			content: "Exams next week",
			topics: [],
			entities: [],
		});
	});
});

describe("correcting", () => {
	test("an edit sends the state's own kind", async () => {
		const brain = fakeBrain();
		const old = brain.state("Blocked on the reviewer");
		openNow(brain);

		const row = within(await entry("Blocked on the reviewer"));
		fireEvent.click(row.getByRole("button", { name: "Edit" }));
		fireEvent.change(screen.getByLabelText("Entry"), {
			target: { value: "Waiting on the reviewer" },
		});
		fireEvent.click(screen.getByRole("button", { name: "Save" }));

		await entry("Waiting on the reviewer");
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: `/brain/api/items/${old.id}/edit`,
				body: { kind: "state", content: "Waiting on the reviewer" },
			},
		]);
	});
});

describe("when it was last confirmed", () => {
	const today = new Date(2026, 10, 13, 15, 30);

	test("today, then the day and month, with the year when it is another", () => {
		expect(confirmedLabel(new Date(2026, 10, 13, 0, 5), today)).toBe(
			"Confirmed today",
		);
		expect(confirmedLabel(new Date(2026, 10, 12, 23, 59), today)).toBe(
			"Confirmed 12 Nov",
		);
		expect(confirmedLabel(new Date(2025, 7, 3, 12, 0), today)).toBe(
			"Confirmed 3 Aug 2025",
		);
	});
});
