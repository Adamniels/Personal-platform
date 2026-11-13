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
import { today } from "./when";

afterEach(cleanup);

function open(brain: ReturnType<typeof fakeBrain>, path: string) {
	window.history.pushState(null, "", path);
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
}

async function panel() {
	return within(await screen.findByRole("tabpanel", { name: "History" }));
}

/** Two decisions, the first replaced by the second. */
function replacedPair(brain: ReturnType<typeof fakeBrain>) {
	const old = brain.decided(
		"Use one database per feature",
		{ reasoning: "Isolation", rejected: ["One shared schema"] },
		{ occurredAt: "2026-03-01T00:00:00Z", precision: "day" },
	);
	const newer = brain.decided(
		"Use one Postgres with a database per service",
		{ reasoning: "Fewer moving parts" },
		{ occurredAt: "2026-10-01T00:00:00Z", precision: "day" },
	);
	brain.replace(old, newer);
	return { old, newer };
}

describe("a replaced decision says what replaced it and is not offered for replacing again", () => {
	test("the old one is Replaced, and links to the one that replaced it", async () => {
		const brain = fakeBrain();
		const { old, newer } = replacedPair(brain);
		open(brain, `/brain/history/${old.id}`);

		const page = await panel();
		expect(
			await page.findByRole("heading", {
				name: "Use one database per feature",
				level: 2,
			}),
		).toBeTruthy();
		const replaced = page.getByText("Replaced", { selector: "span" });
		expect(replaced.className).toContain("idle");
		const by = page.getByRole("link", {
			name: "Use one Postgres with a database per service",
		});
		expect(by.getAttribute("href")).toBe(`/brain/history/${newer.id}`);
		expect(by.closest("dd")?.previousElementSibling?.textContent).toBe(
			"Replaced by",
		);
		expect(
			page.queryByRole("button", { name: "Replace with a newer decision…" }),
		).toBeNull();
		// What it said is all still there.
		expect(page.getByText("Isolation")).toBeTruthy();
		expect(page.getByText("One shared schema")).toBeTruthy();
	});

	test("the newer one says what it replaces, and can be replaced in turn", async () => {
		const brain = fakeBrain();
		const { old, newer } = replacedPair(brain);
		open(brain, `/brain/history/${newer.id}`);

		const page = await panel();
		const replaces = await page.findByRole("link", {
			name: "Use one database per feature",
		});
		expect(replaces.getAttribute("href")).toBe(`/brain/history/${old.id}`);
		expect(replaces.closest("dd")?.previousElementSibling?.textContent).toBe(
			"Replaces",
		);
		expect(page.queryByText("Replaced", { selector: "span" })).toBeNull();
		expect(
			page.getByRole("button", { name: "Replace with a newer decision…" }),
		).toBeTruthy();
	});

	test("replacing asks for the newer one from the other active decisions", async () => {
		const brain = fakeBrain();
		const { newer } = replacedPair(brain);
		const newest = brain.decided("Use SQLite", { reasoning: "Simpler" });
		open(brain, `/brain/history/${newer.id}`);

		const page = await panel();
		fireEvent.click(
			await page.findByRole("button", {
				name: "Replace with a newer decision…",
			}),
		);
		const form = within(
			page.getByRole("form", { name: "Replace with a newer decision…" }),
		);
		const select = form.getByLabelText("Newer decision") as HTMLSelectElement;
		// Neither itself nor the one it replaced, which is no longer active.
		expect(
			[...select.options].filter((o) => !o.disabled).map((o) => o.value),
		).toEqual([newest.id]);
		fireEvent.change(select, { target: { value: newest.id } });
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		expect(
			await page.findByText("Replaced", { selector: "span" }),
		).toBeTruthy();
		expect(page.getByRole("link", { name: "Use SQLite" })).toBeTruthy();
		expect(
			page.queryByRole("button", { name: "Replace with a newer decision…" }),
		).toBeNull();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: `/brain/api/items/${newer.id}/replace`,
				body: { replaced_by: newest.id },
			},
		]);
	});

	test("an address that names no decision says so", async () => {
		const brain = fakeBrain();
		const happened = brain.happened(
			"Took the course",
			"2021-01-01T00:00:00Z",
			"year",
		);
		open(brain, `/brain/history/${happened.id}`);

		const page = await panel();
		expect(await page.findByText("There is no such decision")).toBeTruthy();
		expect(
			page.getByRole("link", { name: "Go to History" }).getAttribute("href"),
		).toBe("/brain/history");
	});
});

describe("on the timeline", () => {
	test("a decision links to its page, and a replaced one says so", async () => {
		const brain = fakeBrain();
		const { old, newer } = replacedPair(brain);
		open(brain, "/brain/history");

		const page = await panel();
		const list = within(await page.findByRole("list", { name: "History" }));
		const [first, second] = list.getAllByRole("listitem") as [
			HTMLElement,
			HTMLElement,
		];
		expect(
			within(first)
				.getByRole("link", {
					name: "Use one Postgres with a database per service",
				})
				.getAttribute("href"),
		).toBe(`/brain/history/${newer.id}`);
		expect(within(first).queryByText("Replaced")).toBeNull();
		expect(
			within(second)
				.getByRole("link", { name: "Use one database per feature" })
				.getAttribute("href"),
		).toBe(`/brain/history/${old.id}`);
		expect(within(second).getByText("Replaced")).toBeTruthy();
	});
});

describe("adding a decision", () => {
	test("what, why, what was rejected a line at a time, and today by default", async () => {
		const brain = fakeBrain();
		open(brain, "/brain/history");

		const page = await panel();
		fireEvent.click(await page.findByRole("button", { name: "Add decision" }));
		const form = within(page.getByRole("form", { name: "Add decision" }));
		expect((form.getByLabelText("Day") as HTMLInputElement).value).toBe(
			today(),
		);
		fireEvent.change(form.getByLabelText("What was decided"), {
			target: { value: "The brain is its own service" },
		});
		fireEvent.change(form.getByLabelText("Why"), {
			target: { value: "Splitting memory across languages failed" },
		});
		const rejected = within(
			form.getByRole("group", { name: "What was rejected" }),
		);
		fireEvent.change(rejected.getByLabelText("Option 1"), {
			target: { value: "A memory module in each feature" },
		});
		fireEvent.click(rejected.getByRole("button", { name: "Add another" }));
		const second = rejected.getByLabelText("Option 2");
		expect(document.activeElement).toBe(second);
		fireEvent.change(second, { target: { value: "  " } });
		fireEvent.click(rejected.getByRole("button", { name: "Add another" }));
		fireEvent.change(rejected.getByLabelText("Option 3"), {
			target: { value: "A library" },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		const list = within(await page.findByRole("list", { name: "History" }));
		expect(
			await list.findByRole("link", { name: "The brain is its own service" }),
		).toBeTruthy();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: "/brain/api/items",
				body: {
					kind: "decision",
					content: "The brain is its own service",
					details: {
						reasoning: "Splitting memory across languages failed",
						rejected: ["A memory module in each feature", "A library"],
					},
					occurred_at: `${today()}T00:00:00Z`,
					occurred_precision: "day",
					topics: [],
					entities: [],
				},
			},
		]);
	});

	test("it needs a reason, and sends nothing until it has one", async () => {
		const brain = fakeBrain();
		open(brain, "/brain/history");

		const page = await panel();
		fireEvent.click(await page.findByRole("button", { name: "Add decision" }));
		const form = within(page.getByRole("form", { name: "Add decision" }));
		fireEvent.change(form.getByLabelText("What was decided"), {
			target: { value: "Ship it" },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		expect(form.getByText("Error: Say why it was decided")).toBeTruthy();
		await waitFor(() => expect(brain.changes()).toEqual([]));
	});
});
