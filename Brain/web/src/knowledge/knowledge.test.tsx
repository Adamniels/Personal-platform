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

function openKnowledge(brain: ReturnType<typeof fakeBrain>) {
	window.history.pushState(null, "", "/brain/knowledge");
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
	return within(screen.getByRole("tabpanel", { name: "Knowledge" }));
}

/** The table's body rows, each as its topic, its level and its focus. The
 * first cell also holds the line folded for phone, so its link is read. */
async function rowsOf(knowledge: ReturnType<typeof openKnowledge>) {
	const table = await knowledge.findByRole("table", { name: "Topics" });
	return within(table)
		.getAllByRole("row")
		.slice(1)
		.map((row) => {
			const [topic, level, focus] = within(row).getAllByRole("cell");
			return [
				topic?.querySelector("a")?.textContent,
				level?.textContent,
				focus?.textContent,
			];
		});
}

/** The table, where a row's fields are while it is edited. */
function inTable(knowledge: ReturnType<typeof openKnowledge>) {
	return within(knowledge.getByRole("table", { name: "Topics" }));
}

function row(knowledge: ReturnType<typeof openKnowledge>, topic: string) {
	const link = knowledge.getByRole("link", { name: topic });
	const found = link.closest("tr");
	if (!found) throw new Error(`no row for ${topic}`);
	return within(found);
}

describe("a level and a focus are shown as words", () => {
	test("each of the four levels and three focus values, and Not set", async () => {
		const brain = fakeBrain();
		brain.topic("Algebra", { level: "shallow", focus: "learning" });
		brain.topic("Bash", { level: "working", focus: "steady" });
		brain.topic("COBOL", { level: "solid", focus: "not_pursuing" });
		brain.topic("Databases", { level: "deep" });
		brain.topic("Elixir");
		const knowledge = openKnowledge(brain);

		expect(await rowsOf(knowledge)).toEqual([
			["Algebra", "Shallow", "Learning"],
			["Bash", "Working", "Steady"],
			["COBOL", "Solid", "Not pursuing"],
			["Databases", "Deep", "Not set"],
			["Elixir", "Not set", "Not set"],
		]);
	});
});

describe("the level select explains each level", () => {
	test("each option's help line is its meaning from the knowledge model", async () => {
		const brain = fakeBrain();
		brain.topic("Postgres");
		const knowledge = openKnowledge(brain);

		fireEvent.click(
			await knowledge.findByRole("button", { name: "Edit Postgres" }),
		);
		const select = inTable(knowledge).getByLabelText(
			"Level",
		) as HTMLSelectElement;
		const meanings: Record<string, string> = {};
		for (const option of Array.from(select.options).filter((o) => o.value)) {
			fireEvent.change(select, { target: { value: option.value } });
			const help = select.getAttribute("aria-describedby") ?? "";
			meanings[option.text] =
				document.getElementById(help.split(" ").at(-1) ?? "")?.textContent ??
				"";
		}

		expect(meanings).toEqual({
			Shallow: "Knows the vocabulary",
			Working: "Can use it with help",
			Solid: "Can use it unaided and explain it",
			Deep: "Knows the internals and could teach it",
		});
	});
});

describe("the table sorts by level in the order of the levels, not the alphabet", () => {
	test("shallow, working, solid, deep, then Not set, and back the other way", async () => {
		const brain = fakeBrain();
		brain.topic("Algebra", { level: "working" });
		brain.topic("Bash");
		brain.topic("COBOL", { level: "deep" });
		brain.topic("Databases", { level: "shallow" });
		brain.topic("Elixir", { level: "solid" });
		const knowledge = openKnowledge(brain);

		// Sorted by topic at first.
		expect((await rowsOf(knowledge)).map(([name]) => name)).toEqual([
			"Algebra",
			"Bash",
			"COBOL",
			"Databases",
			"Elixir",
		]);

		fireEvent.click(knowledge.getByRole("button", { name: "Level" }));
		expect((await rowsOf(knowledge)).map(([, level]) => level)).toEqual([
			"Shallow",
			"Working",
			"Solid",
			"Deep",
			"Not set",
		]);

		fireEvent.click(knowledge.getByRole("button", { name: "Level" }));
		// The other way round; a topic with no level still comes last.
		expect((await rowsOf(knowledge)).map(([, level]) => level)).toEqual([
			"Deep",
			"Solid",
			"Working",
			"Shallow",
			"Not set",
		]);
	});
});

describe("editing a row", () => {
	test("sends only what changed, a level and a focus each on the topic's slug", async () => {
		const brain = fakeBrain();
		brain.topic("Postgres", { level: "working", focus: "learning" });
		const knowledge = openKnowledge(brain);

		fireEvent.click(
			await knowledge.findByRole("button", { name: "Edit Postgres" }),
		);
		// Saved as it is, nothing is sent.
		fireEvent.click(knowledge.getByRole("button", { name: "Save" }));
		await waitFor(() =>
			expect(inTable(knowledge).queryByLabelText("Level")).toBeNull(),
		);
		expect(brain.changes()).toEqual([]);

		fireEvent.click(knowledge.getByRole("button", { name: "Edit Postgres" }));
		fireEvent.change(inTable(knowledge).getByLabelText("Level"), {
			target: { value: "solid" },
		});
		fireEvent.click(knowledge.getByRole("button", { name: "Save" }));

		await waitFor(() =>
			expect(row(knowledge, "Postgres").getByText("Solid")).toBeTruthy(),
		);
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: "/brain/api/items",
				body: { kind: "topic_level", topic: "postgres", value: "solid" },
			},
		]);
	});

	test("a topic with neither offers Not set until a value is chosen, and sends nothing for it", async () => {
		const brain = fakeBrain();
		brain.topic("Postgres");
		const knowledge = openKnowledge(brain);

		fireEvent.click(
			await knowledge.findByRole("button", { name: "Edit Postgres" }),
		);
		const focus = inTable(knowledge).getByLabelText(
			"Focus",
		) as HTMLSelectElement;
		expect(focus.value).toBe("");
		expect(focus.options[focus.selectedIndex]?.text).toBe("Not set");
		fireEvent.change(focus, { target: { value: "not_pursuing" } });
		fireEvent.click(knowledge.getByRole("button", { name: "Save" }));

		await waitFor(() =>
			expect(row(knowledge, "Postgres").getByText("Not pursuing")).toBeTruthy(),
		);
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: "/brain/api/items",
				body: { kind: "topic_focus", topic: "postgres", value: "not_pursuing" },
			},
		]);
	});
});
