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

function open(brain: ReturnType<typeof fakeBrain>, address: string) {
	window.history.pushState(null, "", address);
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
	return within(screen.getByRole("tabpanel", { name: "Everything" }));
}

/** The entries the table shows, by the text of each row's link. */
async function entriesOf(everything: ReturnType<typeof open>) {
	const table = await everything.findByRole("table", { name: "Everything" });
	return within(table)
		.getAllByRole("row")
		.slice(1)
		.map((row) => row.querySelector("a")?.textContent);
}

/** Some of every kind and state: two rules, one retired, a profile entry
 * corrected once, and a state about Rust. */
function stocked() {
	const brain = fakeBrain();
	brain.topic("Rust");
	brain.add("rule", "Answer in English");
	const retired = brain.add("rule", "Keep answers short");
	retired.status = "inactive";
	retired.inactive_reason = "retired";
	const first = brain.add("profile", "Honesty over comfort", "values");
	first.status = "inactive";
	first.inactive_reason = "superseded";
	const corrected = brain.add("profile", "Honesty, said kindly", "values");
	corrected.lineage_id = first.lineage_id;
	brain.state("Learning Rust", { topics: ["rust"] });
	return brain;
}

describe("the filters are kept in the address", () => {
	test("a shared link shows the same list", async () => {
		const brain = stocked();
		const everything = open(brain, "/brain/everything?kind=rule&state=retired");

		expect(await entriesOf(everything)).toEqual(["Keep answers short"]);
		expect(everything.getByLabelText("Kind")).toHaveProperty("value", "rule");
		expect(everything.getByLabelText("State")).toHaveProperty(
			"value",
			"retired",
		);
		// Each filter not at its default is a pill too.
		expect(
			everything.getByRole("button", { name: "Remove filter Kind: Rule" }),
		).toBeTruthy();
		expect(
			everything.getByRole("button", { name: "Remove filter State: Retired" }),
		).toBeTruthy();
	});

	test("changing a filter changes the address, and a reload keeps it", async () => {
		const brain = stocked();
		let everything = open(brain, "/brain/everything");
		// By default, what is active and what may no longer be true.
		expect(await entriesOf(everything)).toEqual([
			"Learning Rust",
			"Honesty, said kindly",
			"Answer in English",
		]);

		fireEvent.change(everything.getByLabelText("State"), {
			target: { value: "replaced" },
		});
		expect(window.location.search).toBe("?state=replaced");
		expect(await entriesOf(everything)).toEqual(["Honesty over comfort"]);

		fireEvent.change(everything.getByLabelText("Subject"), {
			target: { value: "topic:rust" },
		});
		fireEvent.change(everything.getByLabelText("State"), {
			target: { value: "all" },
		});
		expect(new URLSearchParams(window.location.search).get("topic")).toBe(
			"rust",
		);
		expect(await entriesOf(everything)).toEqual(["Learning Rust"]);

		cleanup();
		everything = open(brain, window.location.pathname + window.location.search);
		expect(await entriesOf(everything)).toEqual(["Learning Rust"]);
		// Once the topics have loaded, the select names it.
		await waitFor(() =>
			expect(everything.getByLabelText("Subject")).toHaveProperty(
				"value",
				"topic:rust",
			),
		);

		// Removing a pill puts that filter back to its default, and the
		// address says so.
		fireEvent.click(
			everything.getByRole("button", { name: "Remove filter Subject: Rust" }),
		);
		expect(new URLSearchParams(window.location.search).get("topic")).toBeNull();
		expect(await entriesOf(everything)).toHaveLength(5);
	});

	test("the page is in the address, fifty rows to a page", async () => {
		const brain = fakeBrain();
		for (let n = 1; n <= 51; n += 1) brain.add("rule", `Rule ${n}`);
		const everything = open(brain, "/brain/everything?page=2");

		// Newest support first: the oldest rule is alone on page two.
		expect(await entriesOf(everything)).toEqual(["Rule 1"]);
		expect(everything.getByText("Rows 51 to 51 of 51")).toBeTruthy();

		fireEvent.click(everything.getByRole("button", { name: "Previous" }));
		expect(window.location.search).toBe("");
		expect(await entriesOf(everything)).toHaveLength(50);
	});
});
