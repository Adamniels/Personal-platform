import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, test } from "vitest";
import { type FakePlatform, fakePlatform } from "../test/fake-platform";
import { openAt, settle, type } from "../test/render";

const NOW = Date.UTC(2026, 10, 14, 9, 0);
const DAY = 24 * 60 * 60 * 1000;

const SCHEDULING = { slug: "scheduling", name: "Scheduling" };
const DEADLINES = { slug: "deadlines", name: "Deadlines" };

let platform: FakePlatform;

beforeEach(() => {
	platform = fakePlatform({ now: NOW });
	localStorage.clear();
});

afterEach(cleanup);

async function cardsTable() {
	return screen.findByRole("table", { name: "Cards" });
}

/** The body rows, each as the text of its cells. */
async function rows() {
	const table = await cardsTable();
	return within(table)
		.getAllByRole("row")
		.slice(1)
		.map((row) => row);
}

async function rowOf(front: string) {
	const row = (await rows()).find(
		(each) => within(each).queryByText(front) !== null,
	);
	if (!row) throw new Error(`no card ${front}`);
	return row;
}

describe("the Cards tab lists every card", () => {
	test("newest first, with its deck, its topics as chips and when it is next due", async () => {
		const systems = platform.addDeck("Real Time Systems");
		platform.addCard(systems, {
			front: "What is EDF?",
			topics: [SCHEDULING, DEADLINES],
			due: new Date(NOW + 3 * DAY).toISOString(),
		});
		platform.addCard(systems, {
			front: "What is RMS?\nRate monotonic, the second line",
			topics: [SCHEDULING],
			due: new Date(NOW - DAY).toISOString(),
		});

		openAt("/review/cards", platform);
		const [first, second] = await rows();

		expect(within(first as HTMLElement).getByText("What is RMS?")).toBeTruthy();
		expect(
			within(first as HTMLElement).queryByText(/the second line/),
		).toBeNull();
		expect(
			within(second as HTMLElement).getByText("What is EDF?"),
		).toBeTruthy();

		const cells = within(second as HTMLElement).getAllByRole("cell");
		expect(cells[1]?.textContent).toBe("Real Time Systems");
		expect(
			[...(cells[2]?.firstElementChild?.children ?? [])].map(
				(chip) => chip.textContent,
			),
		).toEqual(["Scheduling", "Deadlines"]);
		const due = within(cells[3] as HTMLElement).getByText("in 3 days");
		expect(due.getAttribute("title")).toBeTruthy();
		expect(due.getAttribute("dateTime")).toBe(
			new Date(NOW + 3 * DAY).toISOString(),
		);
		expect(
			within(first as HTMLElement).getAllByRole("cell")[3]?.textContent,
		).toBe("now");
	});

	test("a list that cannot be loaded says so once, in place, not as a toast too", async () => {
		platform.addDeck("Real Time Systems");
		platform.refuse("GET", /\/review\/api\/cards(\?|$)/, 503, {
			title: "Service Unavailable",
		});
		openAt("/review/cards", platform);

		expect(
			await screen.findByText("The cards could not be loaded."),
		).toBeTruthy();
		await settle();
		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("decks that cannot be loaded say so once, in place, not as a toast too", async () => {
		platform.refuse("GET", /\/review\/api\/decks$/, 503, {
			title: "Service Unavailable",
		});
		openAt("/review/cards", platform);

		expect(
			await screen.findByText("The decks could not be loaded."),
		).toBeTruthy();
		await settle();
		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("a long front is cut at 120 characters", async () => {
		const deck = platform.addDeck("Real Time Systems");
		const long = "x".repeat(150);
		platform.addCard(deck, { front: long, topics: [SCHEDULING] });

		openAt("/review/cards", platform);
		const [row] = await rows();

		const front = within(row as HTMLElement).getAllByRole("cell")[0];
		expect(front?.textContent).toContain(`${"x".repeat(120)}…`);
		expect(front?.textContent).not.toContain("x".repeat(121));
	});

	test("filtering by deck asks for that deck's cards", async () => {
		const systems = platform.addDeck("Real Time Systems");
		const compilers = platform.addDeck("Compilers");
		platform.addCard(systems, { front: "What is EDF?", topics: [SCHEDULING] });
		platform.addCard(compilers, {
			front: "What is SSA?",
			topics: [{ slug: "ssa", name: "SSA" }],
		});
		openAt("/review/cards", platform);
		expect(await rows()).toHaveLength(2);

		const filter = screen.getByRole("combobox", {
			name: "Deck",
		}) as HTMLSelectElement;
		expect(filter.value).toBe("");
		expect(filter.selectedOptions[0]?.textContent).toBe("All decks");
		fireEvent.change(filter, { target: { value: compilers.id } });
		await settle();

		const shown = await rows();
		expect(shown).toHaveLength(1);
		expect(
			within(shown[0] as HTMLElement).getByText("What is SSA?"),
		).toBeTruthy();
		expect(
			platform.calls.some(
				(call) => call.path === `/review/api/cards?deck=${compilers.id}`,
			),
		).toBe(true);
	});
});

describe("a card is edited in place and deleted after asking", () => {
	test("Edit opens the form in the card's row, and Save changes it", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const card = platform.addCard(deck, {
			front: "What is EDF?",
			back: "Earliest deadline first",
			topics: [SCHEDULING],
		});
		openAt("/review/cards", platform);

		fireEvent.click(
			within(await rowOf("What is EDF?")).getByRole("button", { name: "Edit" }),
		);
		const row = (await rows())[0] as HTMLElement;
		const edit = within(row).getByRole("form", { name: "Edit card" });
		const front = within(edit).getByRole("textbox", {
			name: "Front",
		}) as HTMLTextAreaElement;
		expect(front.value).toBe("What is EDF?");
		expect(document.activeElement).toBe(front);
		expect(
			(
				within(edit).getByRole("textbox", {
					name: "Back",
				}) as HTMLTextAreaElement
			).value,
		).toBe("Earliest deadline first");
		await screen.findByRole("button", { name: "Remove Scheduling" });

		type(front, "What does EDF schedule by?");
		fireEvent.click(within(edit).getByRole("button", { name: "Save" }));

		expect(await rowOf("What does EDF schedule by?")).toBeTruthy();
		expect(screen.queryByRole("form", { name: "Edit card" })).toBeNull();
		expect(platform.callsTo("PATCH", "/review/api/cards")).toEqual([
			{
				method: "PATCH",
				path: `/review/api/cards/${card.id}`,
				body: {
					version: 1,
					deck_id: deck.id,
					front: "What does EDF schedule by?",
					back: "Earliest deadline first",
					topics: [SCHEDULING],
				},
			},
		]);
	});

	test("a card changed since it was read is refused, and editing it again starts from the current card", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		const card = platform.addCard(deck, {
			front: "What is EDF?",
			back: "Earliest deadline first",
			topics: [SCHEDULING],
		});
		openAt("/review/cards", platform);
		await rowOf("What is EDF?");

		// Changed in another tab after the list was read.
		Object.assign(card, { back: "Changed elsewhere", version: 2 });

		fireEvent.click(
			within(await rowOf("What is EDF?")).getByRole("button", { name: "Edit" }),
		);
		let edit = screen.getByRole("form", { name: "Edit card" });
		await within(edit).findByRole("button", { name: "Remove Scheduling" });
		fireEvent.click(within(edit).getByRole("button", { name: "Save" }));

		const refused = await screen.findByRole("alert");
		expect(refused.textContent).toContain(
			"This card changed since it was opened",
		);
		await settle();

		fireEvent.click(within(edit).getByRole("button", { name: "Close" }));
		fireEvent.click(
			within(await rowOf("What is EDF?")).getByRole("button", { name: "Edit" }),
		);
		edit = screen.getByRole("form", { name: "Edit card" });
		expect(
			(
				within(edit).getByRole("textbox", {
					name: "Back",
				}) as HTMLTextAreaElement
			).value,
		).toBe("Changed elsewhere");
		await within(edit).findByRole("button", { name: "Remove Scheduling" });
		type(
			within(edit).getByRole("textbox", { name: "Front" }),
			"What does EDF schedule by?",
		);
		fireEvent.click(within(edit).getByRole("button", { name: "Save" }));

		expect(await rowOf("What does EDF schedule by?")).toBeTruthy();
		const patches = platform.callsTo("PATCH", "/review/api/cards");
		expect(
			patches.map((call) => (call.body as { version: number }).version),
		).toEqual([1, 2]);
	});

	test("Delete asks first, then deletes the card and says so", async () => {
		const deck = platform.addDeck("Real Time Systems");
		const card = platform.addCard(deck, {
			front: "What is EDF?",
			topics: [SCHEDULING],
		});
		openAt("/review/cards", platform);

		fireEvent.click(
			within(await rowOf("What is EDF?")).getByRole("button", {
				name: "Delete",
			}),
		);
		const dialog = screen.getByRole("dialog", { name: "Delete this card?" });
		expect(
			within(dialog).getByText("Its review history goes with it."),
		).toBeTruthy();
		expect(platform.callsTo("DELETE", "/review/api/cards")).toEqual([]);

		fireEvent.click(
			within(dialog).getByRole("button", { name: "Delete card" }),
		);
		await settle();

		expect(platform.callsTo("DELETE", "/review/api/cards")).toEqual([
			{
				method: "DELETE",
				path: `/review/api/cards/${card.id}`,
				body: undefined,
			},
		]);
		const toast = await screen.findByRole("status");
		expect(toast.textContent).toContain("Card deleted");
		expect(within(toast).queryByRole("button", { name: "Undo" })).toBeNull();
		expect(await screen.findByText("No cards yet")).toBeTruthy();
	});
});

describe("the Cards tab with nothing in it", () => {
	test("with no decks, it says to make a deck first, with a link to Decks", async () => {
		openAt("/review/cards", platform);

		expect(await screen.findByText("Make a deck first")).toBeTruthy();
		const link = screen.getByRole("link", { name: "Go to Decks" });
		expect(link.getAttribute("href")).toBe("/review/decks");
		expect(screen.queryByRole("button", { name: "Add card" })).toBeNull();

		fireEvent.click(link);
		expect(window.location.pathname).toBe("/review/decks");
	});

	test("with decks and no cards, it says so, with Add card", async () => {
		platform.addDeck("Real Time Systems");
		openAt("/review/cards", platform);

		expect(await screen.findByText("No cards yet")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Add card" }));

		expect(screen.getByRole("form", { name: "New card" })).toBeTruthy();
	});
});
