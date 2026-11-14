import { cleanup, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { fakePlatform } from "../test/fake-platform";
import { openAt } from "../test/render";

afterEach(cleanup);

const HOUR = 60 * 60_000;
const DAY = 24 * HOUR;

/** A platform with cards in two decks, due now under two topics, a card
 * with both topics, one not due yet, and a deck with nothing due. */
function platformWithDueCards() {
	const platform = fakePlatform();
	const compilers = platform.addDeck("Compilers");
	const databases = platform.addDeck("Databases");
	const later = platform.addDeck("Later");
	const parsing = { slug: "parsing", name: "Parsing" };
	const mvcc = { slug: "mvcc", name: "MVCC" };
	const due = platform.at(-HOUR);
	platform.addCard(compilers, { front: "LL(1)?", topics: [parsing], due });
	platform.addCard(compilers, { front: "LR(1)?", topics: [parsing], due });
	platform.addCard(databases, { front: "Snapshot?", topics: [mvcc], due });
	platform.addCard(databases, {
		front: "Parsing SQL under MVCC?",
		topics: [mvcc, parsing],
		due,
	});
	platform.addCard(later, {
		front: "Not yet",
		topics: [{ slug: "later", name: "Later" }],
		due: platform.at(3 * DAY),
	});
	return { platform, compilers, databases };
}

/** Waits for the queue to be read, or to fail. */
async function loaded() {
	await waitFor(() =>
		expect(screen.queryByText("Loading what is due…")).toBeNull(),
	);
}

function row(list: HTMLElement, name: string) {
	const item = within(list)
		.getAllByRole("listitem")
		.find((each) => within(each).queryByText(name) !== null);
	if (!item) throw new Error(`no row for ${name}`);
	return item;
}

describe("the Study tab", () => {
	it("shows how many cards are due, by deck and by topic", async () => {
		const { platform, compilers, databases } = platformWithDueCards();

		openAt("/review", platform);
		await loaded();

		expect(
			screen.getByRole("heading", { level: 2, name: "4 cards due" }),
		).toBeTruthy();
		const everything = screen.getByRole("link", { name: "Start everything" });
		expect(everything.getAttribute("href")).toBe("/review/study");

		const byDeck = screen.getByRole("list", { name: "By deck" });
		expect(within(byDeck).getAllByRole("listitem")).toHaveLength(2);
		const compilersRow = row(byDeck, "Compilers");
		expect(compilersRow.textContent).toContain("2 due");
		expect(
			within(compilersRow)
				.getByRole("link", { name: "Start" })
				.getAttribute("href"),
		).toBe(`/review/study?deck=${compilers.id}`);
		expect(row(byDeck, "Databases").textContent).toContain("2 due");
		expect(
			within(row(byDeck, "Databases"))
				.getByRole("link", { name: "Start" })
				.getAttribute("href"),
		).toBe(`/review/study?deck=${databases.id}`);
		// A deck with nothing due is not listed.
		expect(within(byDeck).queryByText("Later")).toBeNull();

		// A card with two topics counts under both.
		const byTopic = screen.getByRole("list", { name: "By topic" });
		expect(within(byTopic).getAllByRole("listitem")).toHaveLength(2);
		const parsingRow = row(byTopic, "Parsing");
		expect(parsingRow.textContent).toContain("3 due");
		expect(
			within(parsingRow)
				.getByRole("link", { name: "Start" })
				.getAttribute("href"),
		).toBe("/review/study?topic=parsing");
		expect(row(byTopic, "MVCC").textContent).toContain("2 due");

		// Counted from one call, the queue without a filter.
		expect(
			platform.callsTo("GET", "/review/api/queue").map(({ path }) => path),
		).toEqual(["/review/api/queue"]);
	});

	it("names each Start by its deck or topic", async () => {
		const { platform } = platformWithDueCards();

		openAt("/review", platform);
		await loaded();

		const byDeck = screen.getByRole("list", { name: "By deck" });
		const start = within(row(byDeck, "Compilers")).getByRole("link", {
			name: "Start",
		});
		const described = (start.getAttribute("aria-describedby") ?? "")
			.split(" ")
			.map((id) => document.getElementById(id)?.textContent)
			.join(" ");
		expect(described).toContain("Compilers");
	});

	it("says a single card is due in the singular", async () => {
		const platform = fakePlatform();
		const deck = platform.addDeck("Compilers");
		platform.addCard(deck, {
			front: "LL(1)?",
			topics: [{ slug: "parsing", name: "Parsing" }],
			due: platform.at(-HOUR),
		});

		openAt("/review", platform);
		await loaded();

		expect(
			screen.getByRole("heading", { level: 2, name: "1 card due" }),
		).toBeTruthy();
	});

	it("says when nothing is due, and when the next card is", async () => {
		const platform = fakePlatform();
		const deck = platform.addDeck("Compilers");
		platform.addCard(deck, {
			front: "LL(1)?",
			topics: [{ slug: "parsing", name: "Parsing" }],
			due: platform.at(3 * DAY),
		});

		openAt("/review", platform);
		await loaded();

		expect(screen.getByText("Nothing due")).toBeTruthy();
		expect(screen.getByText("The next card is due in 3 days.")).toBeTruthy();
		expect(
			screen.getByRole("link", { name: "Go to Cards" }).getAttribute("href"),
		).toBe("/review/cards");
		expect(screen.queryByRole("link", { name: "Start everything" })).toBeNull();
	});

	it("says when there are no cards at all", async () => {
		const platform = fakePlatform();

		openAt("/review", platform);
		await loaded();

		expect(screen.getByText("Nothing due")).toBeTruthy();
		expect(
			screen.getByText("You have no cards yet. Add some in Cards."),
		).toBeTruthy();
		expect(screen.getByRole("link", { name: "Go to Cards" })).toBeTruthy();
	});

	it("says the lists count the first 200 when more are due", async () => {
		const platform = fakePlatform();
		const deck = platform.addDeck("Many");
		for (let n = 0; n < 205; n += 1) {
			platform.addCard(deck, {
				front: `Card ${n}`,
				topics: [{ slug: "many", name: "Many" }],
				due: platform.at(-HOUR),
			});
		}

		openAt("/review", platform);
		await loaded();

		expect(
			await screen.findByRole("heading", { level: 2, name: "205 cards due" }),
		).toBeTruthy();
		expect(screen.getByText("The lists count the first 200.")).toBeTruthy();
		expect(
			row(screen.getByRole("list", { name: "By deck" }), "Many").textContent,
		).toContain("200 due");
	});

	it("says so when what is due cannot be loaded", async () => {
		const platform = fakePlatform();
		platform.fail("GET", /\/review\/api\/queue/);

		openAt("/review", platform);
		await loaded();

		expect(
			await screen.findByText("What is due could not be loaded."),
		).toBeTruthy();
	});
});
