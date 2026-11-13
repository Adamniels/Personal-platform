import {
	cleanup,
	fireEvent,
	render,
	screen,
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
