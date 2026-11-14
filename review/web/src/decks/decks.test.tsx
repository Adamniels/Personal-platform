import {
	cleanup,
	fireEvent,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, test } from "vitest";
import { type FakePlatform, fakePlatform } from "../test/fake-platform";
import { descriptionOf, openAt, settle, type } from "../test/render";

let platform: FakePlatform;

beforeEach(() => {
	platform = fakePlatform();
	localStorage.clear();
});

afterEach(cleanup);

async function decksList() {
	return screen.findByRole("list", { name: "Decks" });
}

/** The deck's row, once the list shows it. */
async function deckRow(name: string) {
	return waitFor(() => {
		const row = within(screen.getByRole("list", { name: "Decks" }))
			.getAllByRole("listitem")
			.find((item) => within(item).queryByText(name) !== null);
		if (!row) throw new Error(`no deck named ${name}`);
		return row;
	});
}

describe("the Decks tab lists the decks with their card counts", () => {
	test("by name, each with its count", async () => {
		const systems = platform.addDeck("Real Time Systems");
		platform.addDeck("Compilers");
		const topic = { slug: "scheduling", name: "Scheduling" };
		platform.addCard(systems, { front: "What is EDF?", topics: [topic] });
		platform.addCard(systems, { front: "What is RMS?", topics: [topic] });

		openAt("/review/decks", platform);

		const rows = within(await decksList()).getAllByRole("listitem");
		expect(rows.map((row) => row.firstElementChild?.textContent)).toEqual([
			"Compilers",
			"Real Time Systems",
		]);
		expect(within(rows[0] as HTMLElement).getByText("0 cards")).toBeTruthy();
		expect(within(rows[1] as HTMLElement).getByText("2 cards")).toBeTruthy();
	});

	test("a list that cannot be loaded says so once, in place, not as a toast too", async () => {
		platform.refuse("GET", /\/review\/api\/decks$/, 503, {
			title: "Service Unavailable",
		});
		openAt("/review/decks", platform);

		expect(
			await screen.findByText("The decks could not be loaded."),
		).toBeTruthy();
		await settle();
		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("with no decks, it says so", async () => {
		openAt("/review/decks", platform);

		expect(await screen.findByText("No decks yet")).toBeTruthy();
		expect(screen.getByRole("button", { name: "Add deck" })).toBeTruthy();
	});
});

describe("Add deck, rename in place and delete", () => {
	test("Add deck opens a form whose Save makes the deck", async () => {
		platform.addDeck("Compilers");
		openAt("/review/decks", platform);
		await decksList();

		fireEvent.click(screen.getByRole("button", { name: "Add deck" }));
		const name = screen.getByRole("textbox", { name: "Name" });
		expect(document.activeElement).toBe(name);
		type(name, "  Real Time Systems ");
		fireEvent.click(screen.getByRole("button", { name: "Save" }));

		expect(await deckRow("Real Time Systems")).toBeTruthy();
		expect(platform.callsTo("POST", "/review/api/decks")).toEqual([
			{
				method: "POST",
				path: "/review/api/decks",
				body: { name: "Real Time Systems" },
			},
		]);
		expect(screen.queryByRole("textbox", { name: "Name" })).toBeNull();
	});

	test("an empty name is refused in the form", async () => {
		openAt("/review/decks", platform);
		await screen.findByText("No decks yet");

		fireEvent.click(screen.getByRole("button", { name: "Add deck" }));
		fireEvent.click(screen.getByRole("button", { name: "Save" }));
		await settle();

		const name = screen.getByRole("textbox", { name: "Name" });
		expect(name.getAttribute("aria-invalid")).toBe("true");
		expect(descriptionOf(name)).toContain("Error: Name the deck");
		expect(platform.callsTo("POST", "/review/api/decks")).toEqual([]);
	});

	test("Rename opens the name in place, and Save renames it", async () => {
		const deck = platform.addDeck("Real Time Sytems");
		openAt("/review/decks", platform);

		fireEvent.click(
			within(await deckRow("Real Time Sytems")).getByRole("button", {
				name: "Rename",
			}),
		);
		const name = within(await decksList()).getByRole("textbox", {
			name: "Name",
		}) as HTMLInputElement;
		expect(name.value).toBe("Real Time Sytems");
		type(name, "Real Time Systems");
		fireEvent.click(screen.getByRole("button", { name: "Save" }));

		expect(await deckRow("Real Time Systems")).toBeTruthy();
		expect(platform.callsTo("PATCH", "/review/api/decks")).toEqual([
			{
				method: "PATCH",
				path: `/review/api/decks/${deck.id}`,
				body: { name: "Real Time Systems" },
			},
		]);
	});

	test("Delete asks first, and deletes the deck once confirmed", async () => {
		const deck = platform.addDeck("Compilers");
		platform.addDeck("Real Time Systems");
		openAt("/review/decks", platform);

		fireEvent.click(
			within(await deckRow("Compilers")).getByRole("button", {
				name: "Delete",
			}),
		);
		const dialog = screen.getByRole("dialog", { name: "Delete this deck?" });
		fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
		await settle();
		expect(platform.callsTo("DELETE", "/review/api/decks")).toEqual([]);

		fireEvent.click(
			within(await deckRow("Compilers")).getByRole("button", {
				name: "Delete",
			}),
		);
		fireEvent.click(
			within(
				screen.getByRole("dialog", { name: "Delete this deck?" }),
			).getByRole("button", { name: "Delete deck" }),
		);
		await settle();

		expect(platform.callsTo("DELETE", "/review/api/decks")).toEqual([
			{
				method: "DELETE",
				path: `/review/api/decks/${deck.id}`,
				body: undefined,
			},
		]);
		expect(
			within(await decksList()).queryByText("Compilers", { exact: true }),
		).toBeNull();
		expect((await screen.findByRole("status")).textContent).toContain(
			"Deck deleted",
		);
	});
});

describe("a refusal says why", () => {
	test("a duplicate name shows the 409 on the field, without a toast", async () => {
		platform.addDeck("Real Time Systems");
		openAt("/review/decks", platform);
		await decksList();

		fireEvent.click(screen.getByRole("button", { name: "Add deck" }));
		type(screen.getByRole("textbox", { name: "Name" }), "real time systems");
		fireEvent.click(screen.getByRole("button", { name: "Save" }));
		await settle();

		const name = screen.getByRole("textbox", { name: "Name" });
		expect(name.getAttribute("aria-invalid")).toBe("true");
		expect(descriptionOf(name)).toContain(
			"Error: You already have a deck named real time systems.",
		);
		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("a deck with cards cannot be deleted, and its Delete says why", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addCard(deck, {
			front: "What is EDF?",
			topics: [{ slug: "scheduling", name: "Scheduling" }],
		});
		openAt("/review/decks", platform);

		const remove = within(await deckRow("Real Time Systems")).getByRole(
			"button",
			{ name: "Delete" },
		) as HTMLButtonElement;

		expect(remove.disabled).toBe(true);
		expect(descriptionOf(remove)).toBe("Move or delete its cards first.");
	});
});
