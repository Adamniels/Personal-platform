import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, test } from "vitest";
import { type FakePlatform, fakePlatform } from "../test/fake-platform";
import {
	descriptionOf,
	form,
	openAt,
	picker,
	settle,
	type,
} from "../test/render";

let platform: FakePlatform;

beforeEach(() => {
	platform = fakePlatform();
	localStorage.clear();
});

afterEach(cleanup);

/** Opens Cards and its add form, once the topics have loaded. */
async function openForm() {
	openAt("/review/cards", platform);
	fireEvent.click(await screen.findByRole("button", { name: "Add card" }));
	const add = form("New card");
	await settle();
	return add;
}

function field(add: HTMLElement, name: string) {
	return within(add).getByRole(name === "Deck" ? "combobox" : "textbox", {
		name,
	}) as HTMLInputElement;
}

function pickTopic(add: HTMLElement, text: string) {
	const { field } = picker(add);
	type(field, text);
	fireEvent.keyDown(field, { key: "Enter" });
}

describe("Front, Back, Deck and Topics are required", () => {
	test("Save with every field empty refuses each in the form, and sends nothing", async () => {
		platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const add = await openForm();

		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(descriptionOf(field(add, "Front"))).toContain(
			"Error: Write the front",
		);
		expect(descriptionOf(field(add, "Back"))).toContain(
			"Error: Write the back",
		);
		expect(descriptionOf(field(add, "Deck"))).toContain("Error: Choose a deck");
		expect(descriptionOf(picker(add).field)).toContain(
			"Error: Pick at least one topic",
		);
		for (const name of ["Front", "Back", "Deck"]) {
			expect(field(add, name).getAttribute("aria-invalid")).toBe("true");
		}
		expect(picker(add).field.getAttribute("aria-invalid")).toBe("true");
		expect(platform.callsTo("POST", "/review/api/cards")).toEqual([]);
	});

	test("text of only spaces is empty", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const add = await openForm();

		type(field(add, "Front"), "   ");
		type(field(add, "Back"), "An answer");
		fireEvent.change(field(add, "Deck"), { target: { value: deck.id } });
		pickTopic(add, "Sched");
		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(descriptionOf(field(add, "Front"))).toContain(
			"Error: Write the front",
		);
		expect(platform.callsTo("POST", "/review/api/cards")).toEqual([]);
	});
});

describe("after saving, the form stays open for the next card", () => {
	test("deck and topics kept, Front and Back cleared, focus on Front", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const add = await openForm();

		type(field(add, "Front"), "  What is EDF?  ");
		type(field(add, "Back"), "Earliest deadline first");
		fireEvent.change(field(add, "Deck"), { target: { value: deck.id } });
		pickTopic(add, "Sched");
		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(platform.callsTo("POST", "/review/api/cards")).toEqual([
			{
				method: "POST",
				path: "/review/api/cards",
				body: {
					deck_id: deck.id,
					front: "What is EDF?",
					back: "Earliest deadline first",
					topics: [{ slug: "scheduling", name: "Scheduling" }],
				},
			},
		]);
		const still = form("New card");
		expect(field(still, "Front").value).toBe("");
		expect(field(still, "Back").value).toBe("");
		expect(field(still, "Deck").value).toBe(deck.id);
		expect(picker(still).picked()).toEqual(["Scheduling"]);
		expect(document.activeElement).toBe(field(still, "Front"));
		expect((await screen.findByRole("status")).textContent).toContain(
			"Card added",
		);
		expect(
			await within(
				await screen.findByRole("table", { name: "Cards" }),
			).findByText("What is EDF?"),
		).toBeTruthy();
	});

	test("Ctrl or Cmd with Enter saves", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const add = await openForm();
		type(field(add, "Front"), "What is EDF?");
		type(field(add, "Back"), "Earliest deadline first");
		fireEvent.change(field(add, "Deck"), { target: { value: deck.id } });
		pickTopic(add, "Sched");

		fireEvent.keyDown(field(add, "Back"), { key: "Enter", ctrlKey: true });
		await settle();
		type(field(add, "Front"), "What is RMS?");
		type(field(add, "Back"), "Rate monotonic");
		fireEvent.keyDown(field(add, "Front"), { key: "Enter", metaKey: true });
		await settle();

		expect(platform.callsTo("POST", "/review/api/cards")).toHaveLength(2);
	});

	test("Close closes the form", async () => {
		platform.addDeck("Real Time Systems");
		const add = await openForm();

		fireEvent.click(within(add).getByRole("button", { name: "Close" }));

		expect(screen.queryByRole("form", { name: "New card" })).toBeNull();
		expect(screen.getByRole("button", { name: "Add card" })).toBeTruthy();
	});
});

describe("a refusal shows on its field", () => {
	test("a 422 naming the back shows on Back, without a toast", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const add = await openForm();
		type(field(add, "Front"), "What is EDF?");
		type(field(add, "Back"), "Earliest deadline first");
		fireEvent.change(field(add, "Deck"), { target: { value: deck.id } });
		pickTopic(add, "Sched");
		platform.refuse("POST", /\/review\/api\/cards$/, 422, {
			title: "One or more validation errors occurred.",
			status: 422,
			detail: "The back is 1 to 4000 characters.",
			errors: { back: ["The back is 1 to 4000 characters."] },
		});

		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(field(add, "Back").getAttribute("aria-invalid")).toBe("true");
		expect(descriptionOf(field(add, "Back"))).toContain(
			"Error: The back is 1 to 4000 characters.",
		);
		expect(field(add, "Back").value).toBe("Earliest deadline first");
		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("a 422 naming a topic shows on Topics", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const add = await openForm();
		type(field(add, "Front"), "What is EDF?");
		type(field(add, "Back"), "Earliest deadline first");
		fireEvent.change(field(add, "Deck"), { target: { value: deck.id } });
		pickTopic(add, "Sched");
		platform.refuse("POST", /\/review\/api\/cards$/, 422, {
			status: 422,
			detail: "Not a topic's slug.",
			errors: { "topics[0].slug": ["Not a topic's slug."] },
		});

		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(descriptionOf(picker(add).field)).toContain(
			"Error: Not a topic's slug.",
		);
	});
});

describe("New deck… makes the deck on save", () => {
	test("it turns into a field for the name, and Save makes the deck, then the card in it", async () => {
		platform.addDeck("Compilers");
		platform.addTopic("Scheduling");
		const add = await openForm();
		type(field(add, "Front"), "What is EDF?");
		type(field(add, "Back"), "Earliest deadline first");
		const deck = field(add, "Deck");
		expect(
			[...(deck as unknown as HTMLSelectElement).options].map(
				(option) => option.textContent,
			),
		).toEqual(["Choose a deck", "Compilers", "New deck…"]);

		fireEvent.change(deck, { target: { value: "new" } });
		type(field(add, "New deck name"), "Real Time Systems");
		pickTopic(add, "Sched");
		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		const made = platform.decks.find(
			(each) => each.name === "Real Time Systems",
		);
		expect(made).toBeTruthy();
		expect(
			platform.callsTo("POST", "/review/api/cards").map((call) => call.body),
		).toEqual([
			{
				deck_id: made?.id,
				front: "What is EDF?",
				back: "Earliest deadline first",
				topics: [{ slug: "scheduling", name: "Scheduling" }],
			},
		]);
		expect(field(form("New card"), "Deck").value).toBe(made?.id);
	});

	test("a name the owner already has shows the 409 on the name, and makes no card", async () => {
		platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const add = await openForm();
		type(field(add, "Front"), "What is EDF?");
		type(field(add, "Back"), "Earliest deadline first");
		fireEvent.change(field(add, "Deck"), { target: { value: "new" } });
		type(field(add, "New deck name"), "real time systems");
		pickTopic(add, "Sched");

		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(descriptionOf(field(add, "New deck name"))).toContain(
			"Error: You already have a deck named real time systems.",
		);
		expect(platform.callsTo("POST", "/review/api/cards")).toEqual([]);
		expect(screen.queryByRole("alert")).toBeNull();
	});
});
