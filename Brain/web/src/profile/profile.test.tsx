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

function openProfile(brain: ReturnType<typeof fakeBrain>) {
	window.history.pushState(null, "", "/brain");
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
}

/** The card of a category, once the entries have loaded. */
async function card(name: string) {
	return screen.findByRole("article", { name });
}

describe("the profile section groups entries by category in a fixed order", () => {
	test("six cards, in order, each with its entries or its own empty line", async () => {
		const brain = fakeBrain();
		brain.add("profile", "Honesty over comfort", "values");
		brain.add("profile", "Finish the degree", "goals");
		brain.add("profile", "Say it plainly", "values");
		openProfile(brain);

		await card("Values");
		const titles = screen
			.getAllByRole("article")
			.map((article) => within(article).getByRole("heading").textContent);
		expect(titles).toEqual([
			"Identity",
			"Goals",
			"Values",
			"Interests",
			"Preferences",
			"Working style",
		]);

		const values = within(await card("Values"));
		expect(
			values.getAllByRole("listitem").map((item) => item.textContent),
		).toEqual([
			expect.stringContaining("Honesty over comfort"),
			expect.stringContaining("Say it plainly"),
		]);
		expect(
			within(await card("Goals")).getByText("Finish the degree"),
		).toBeTruthy();
		expect(
			within(await card("Identity")).getByText("No identity entries yet."),
		).toBeTruthy();
		expect(
			within(await card("Working style")).getByText(
				"No working style entries yet.",
			),
		).toBeTruthy();
	});

	test("an edited entry keeps its place, and a retired one is not listed", async () => {
		const brain = fakeBrain();
		const first = brain.add("profile", "Honesty over comfort", "values");
		brain.add("profile", "Say it plainly", "values");
		const gone = brain.add("profile", "Move fast", "values");
		brain.edit(first, "Honesty, kindly");
		gone.status = "inactive";
		gone.inactive_reason = "retired";
		openProfile(brain);

		const values = within(await card("Values"));
		await waitFor(() =>
			expect(
				values.getAllByRole("listitem").map((item) => item.textContent),
			).toEqual([
				expect.stringContaining("Honesty, kindly"),
				expect.stringContaining("Say it plainly"),
			]),
		);
	});
});

describe("adding needs text, and reports the server's refusal", () => {
	test("an empty entry cannot be submitted", async () => {
		const brain = fakeBrain();
		openProfile(brain);

		const values = within(await card("Values"));
		fireEvent.click(values.getByRole("button", { name: "Add" }));
		fireEvent.change(values.getByLabelText("New entry"), {
			target: { value: "   " },
		});
		fireEvent.click(values.getByRole("button", { name: "Save" }));

		const field = values.getByLabelText("New entry");
		expect(field.getAttribute("aria-invalid")).toBe("true");
		expect(values.getByText("Error: Write the entry first")).toBeTruthy();
		expect(brain.changes()).toEqual([]);
	});

	test("a 422 shows its message on the field", async () => {
		const brain = fakeBrain();
		brain.refuseNext(422, "Content is longer than 4000 characters");
		openProfile(brain);

		const values = within(await card("Values"));
		fireEvent.click(values.getByRole("button", { name: "Add" }));
		fireEvent.change(values.getByLabelText("New entry"), {
			target: { value: "Too long, as the server sees it" },
		});
		fireEvent.click(values.getByRole("button", { name: "Save" }));

		expect(
			await values.findByText("Error: Content is longer than 4000 characters"),
		).toBeTruthy();
		expect(
			values.getByLabelText("New entry").getAttribute("aria-invalid"),
		).toBe("true");
		// On the field, and not a second time as a toast.
		expect(screen.queryByRole("alert")).toBeNull();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: "/brain/api/items",
				body: {
					kind: "profile",
					category: "values",
					content: "Too long, as the server sees it",
				},
			},
		]);
	});

	test("an entry added from the section's button asks for its category first", async () => {
		const brain = fakeBrain();
		openProfile(brain);
		await card("Values");

		fireEvent.click(screen.getByRole("button", { name: "Add entry" }));
		const form = within(screen.getByRole("form", { name: "Add entry" }));
		fireEvent.change(form.getByLabelText("New entry"), {
			target: { value: "Rust and systems" },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));
		expect(form.getByText("Error: Choose a category")).toBeTruthy();
		expect(brain.changes()).toEqual([]);

		fireEvent.change(form.getByLabelText("Category"), {
			target: { value: "interests" },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		expect(
			await within(await card("Interests")).findByText("Rust and systems"),
		).toBeTruthy();
		expect((await screen.findByRole("status")).textContent).toContain(
			"Entry saved",
		);
	});
});

describe("editing shows the current text, and saving nothing new is not sent", () => {
	test("the text area holds the entry's text", async () => {
		const brain = fakeBrain();
		brain.add("profile", "Honesty over comfort", "values");
		openProfile(brain);

		const values = within(await card("Values"));
		fireEvent.click(await values.findByRole("button", { name: "Edit" }));

		const field = values.getByLabelText("Entry") as HTMLTextAreaElement;
		expect(field.value).toBe("Honesty over comfort");
	});

	test("saving the same text sends nothing and closes the editor", async () => {
		const brain = fakeBrain();
		brain.add("profile", "Honesty over comfort", "values");
		openProfile(brain);

		const values = within(await card("Values"));
		fireEvent.click(await values.findByRole("button", { name: "Edit" }));
		fireEvent.change(values.getByLabelText("Entry"), {
			target: { value: "  Honesty over comfort " },
		});
		fireEvent.click(values.getByRole("button", { name: "Save" }));

		await waitFor(() => expect(values.queryByLabelText("Entry")).toBeNull());
		expect(values.getByText("Honesty over comfort")).toBeTruthy();
		expect(brain.changes()).toEqual([]);
	});

	test("a changed text is sent as an edit of that entry", async () => {
		const brain = fakeBrain();
		const entry = brain.add("profile", "Honesty over comfort", "values");
		openProfile(brain);

		const values = within(await card("Values"));
		fireEvent.click(await values.findByRole("button", { name: "Edit" }));
		fireEvent.change(values.getByLabelText("Entry"), {
			target: { value: "Honesty, kindly" },
		});
		fireEvent.click(values.getByRole("button", { name: "Save" }));

		expect(await values.findByText("Honesty, kindly")).toBeTruthy();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: `/brain/api/items/${entry.id}/edit`,
				body: { kind: "profile", content: "Honesty, kindly" },
			},
		]);
	});
});

describe("an entry's earlier versions", () => {
	test("only an edited entry offers them, oldest first, each with its date", async () => {
		const brain = fakeBrain();
		const first = brain.add("profile", "Honesty over comfort", "values");
		const second = brain.edit(first, "Honesty, kindly");
		brain.edit(second, "Honesty, said kindly");
		brain.add("profile", "Say it plainly", "values");
		openProfile(brain);

		const values = within(await card("Values"));
		const [edited, plain] = values.getAllByRole("listitem");
		expect(
			within(plain as HTMLElement).queryByRole("button", {
				name: "Earlier versions",
			}),
		).toBeNull();
		const open = within(edited as HTMLElement).getByRole("button", {
			name: "Earlier versions",
		});
		expect(open.getAttribute("aria-expanded")).toBe("false");

		fireEvent.click(open);

		const list = within(
			await values.findByRole("list", { name: "Earlier versions" }),
		);
		const versions = list.getAllByRole("listitem");
		expect(
			versions.map((version) => version.querySelector("p")?.textContent),
		).toEqual(["Honesty over comfort", "Honesty, kindly"]);
		const date = versions[0]?.querySelector("time");
		expect(date?.getAttribute("datetime")).toBe(first.created_at);
		expect(date?.textContent).toMatch(
			/^\d{1,2} [A-Z][a-z]{2,3} 2026, \d{2}:\d{2}$/,
		);
		expect(open.getAttribute("aria-expanded")).toBe("true");
	});
});
