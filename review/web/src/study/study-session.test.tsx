import {
	cleanup,
	fireEvent,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { type FakePlatform, fakePlatform } from "../test/fake-platform";
import { openAt, settle } from "../test/render";

afterEach(cleanup);

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const PARSING = { slug: "parsing", name: "Parsing" };
const MVCC = { slug: "mvcc", name: "MVCC" };

/** `count` cards due now in one deck, the first due first, named "Card 1"
 * and on. */
function platformWith(count: number) {
	const platform = fakePlatform();
	const deck = platform.addDeck("Compilers");
	const cards = Array.from({ length: count }, (_, n) =>
		platform.addCard(deck, {
			front: `Card ${n + 1}`,
			back: `Answer ${n + 1}`,
			topics: [PARSING],
			due: platform.at(-HOUR + n * 1000),
		}),
	);
	return { platform, deck, cards };
}

async function open(platform: FakePlatform, path = "/review/study") {
	openAt(path, platform);
	await loaded();
}

/** Waits for the queue to be read, at the start or again. */
async function loaded() {
	await settle();
	await waitFor(() =>
		expect(screen.queryByText("Loading the cards due…")).toBeNull(),
	);
}

function gradeButtons() {
	return screen
		.queryAllByRole("button")
		.filter((button) => /^(Again|Hard|Good|Easy) · /.test(name(button)));
}

/** A button's accessible name as its text reads, separators included. */
function name(element: HTMLElement): string {
	return element.getAttribute("aria-label") ?? element.textContent ?? "";
}

function grades(platform: FakePlatform) {
	return platform.callsTo("POST", "/review/api/cards/");
}

function press(key: string, init: { repeat?: boolean } = {}) {
	fireEvent.keyDown(document.body, { key, ...init });
}

async function showAndGrade(label: RegExp) {
	fireEvent.click(screen.getByRole("button", { name: "Show answer" }));
	fireEvent.click(screen.getByRole("button", { name: label }));
	await loaded();
}

describe("a study session", () => {
	it("shows the front first, then the back and the four grades", async () => {
		const { platform } = platformWith(1);

		await open(platform);

		const card = screen.getByRole("article", { name: "Compilers" });
		expect(within(card).getByText("Card 1")).toBeTruthy();
		expect(within(card).getByText("Parsing")).toBeTruthy();
		expect(within(card).queryByText("Answer 1")).toBeNull();
		expect(gradeButtons()).toHaveLength(0);

		fireEvent.click(screen.getByRole("button", { name: "Show answer" }));

		expect(within(card).getByText("Answer 1")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Show answer" })).toBeNull();
		expect(gradeButtons().map(name)).toEqual([
			"Again · 10m",
			"Hard · 2d",
			"Good · 4d",
			"Easy · 12d",
		]);
		expect(screen.getByText("Space to show, 1 to 4 to grade")).toBeTruthy();
	});

	it("shows the answer with Space", async () => {
		const { platform } = platformWith(1);
		await open(platform);

		press(" ");

		expect(screen.getByText("Answer 1")).toBeTruthy();
		expect(gradeButtons()).toHaveLength(4);
	});

	it("posts a grade with the card's version and shows the next card", async () => {
		const { platform, cards } = platformWith(3);
		await open(platform);
		expect(screen.getByText("1 of 3")).toBeTruthy();

		await showAndGrade(/^Good · /);

		expect(grades(platform)).toEqual([
			{
				method: "POST",
				path: `/review/api/cards/${cards[0]?.id}/grades`,
				body: { rating: 3, version: 1 },
			},
		]);
		expect(screen.getByText("Card 2")).toBeTruthy();
		expect(screen.queryByText("Answer 2")).toBeNull();
		expect(screen.getByText("2 of 3")).toBeTruthy();
	});

	it("grades with the keys 1 to 4, only once the answer shows", async () => {
		const { platform } = platformWith(4);
		await open(platform);

		press("3");
		await settle();
		expect(grades(platform)).toHaveLength(0);

		for (const key of ["1", "2", "3", "4"]) {
			press(" ");
			press(key);
			await settle();
		}

		expect(grades(platform).map(({ body }) => body)).toEqual([
			{ rating: 1, version: 1 },
			{ rating: 2, version: 1 },
			{ rating: 3, version: 1 },
			{ rating: 4, version: 1 },
		]);
	});

	it("grades once for a key held down, and while a grade is in flight", async () => {
		const { platform } = platformWith(3);
		await open(platform);

		press(" ");
		press("3");
		press("3", { repeat: true });
		press("3");
		expect(
			gradeButtons().every((button) => (button as HTMLButtonElement).disabled),
		).toBe(true);
		await settle();

		expect(grades(platform)).toHaveLength(1);
		expect(screen.getByText("2 of 3")).toBeTruthy();
	});

	it("reads the progress as the card of the total at the start", async () => {
		const { platform } = platformWith(12);
		await open(platform);

		await showAndGrade(/^Good · /);
		await showAndGrade(/^Good · /);

		expect(screen.getByText("3 of 12")).toBeTruthy();
	});

	it("says how many were reviewed and when the next is due when done", async () => {
		const { platform } = platformWith(2);
		await open(platform);

		await showAndGrade(/^Good · /);
		await showAndGrade(/^Hard · /);

		expect(screen.getByRole("heading", { name: "Done for now" })).toBeTruthy();
		expect(screen.getByText("You reviewed 2 cards.")).toBeTruthy();
		expect(screen.getByText("The next card is due in 2 days.")).toBeTruthy();
		expect(
			screen.getByRole("link", { name: "Back to Study" }).getAttribute("href"),
		).toBe("/review");
		expect(
			screen.getByRole("link", { name: "Go to Cards" }).getAttribute("href"),
		).toBe("/review/cards");
	});

	it("brings a card graded Again back within the session", async () => {
		const { platform } = platformWith(2);
		await open(platform);

		await showAndGrade(/^Again · /);
		// The card is due again by the time the session runs out.
		platform.advance(10 * 60_000);
		await showAndGrade(/^Good · /);

		expect(screen.getByText("Card 1")).toBeTruthy();
		expect(screen.getByText("3 of 2")).toBeTruthy();
		await showAndGrade(/^Good · /);
		expect(screen.getByText("You reviewed 3 cards.")).toBeTruthy();
	});

	it("fetches the queue at the start, after every 20 grades and when it runs out", async () => {
		const { platform } = platformWith(25);
		await open(platform);
		const queues = () => platform.callsTo("GET", "/review/api/queue").length;
		expect(queues()).toBe(1);

		for (let n = 0; n < 19; n += 1) await showAndGrade(/^Good · /);
		expect(queues()).toBe(1);
		await showAndGrade(/^Good · /);
		expect(queues()).toBe(2);
		expect(screen.getByText("21 of 25")).toBeTruthy();

		for (let n = 0; n < 5; n += 1) await showAndGrade(/^Good · /);
		expect(queues()).toBe(3);
		expect(screen.getByText("You reviewed 25 cards.")).toBeTruthy();
	});

	it("goes on after a 409 with a toast, never grading twice", async () => {
		const { platform, cards } = platformWith(2);
		await open(platform);
		// The first card is changed elsewhere after the session read it.
		const first = platform.cards.find(({ id }) => id === cards[0]?.id);
		if (first) first.version = 2;

		await showAndGrade(/^Good · /);

		expect(screen.getByRole("alert").textContent).toContain(
			"This card changed elsewhere",
		);
		expect(grades(platform)).toHaveLength(1);
		expect(platform.callsTo("GET", "/review/api/queue")).toHaveLength(2);
		// The queue, read again, still holds the changed card, at its new version.
		expect(screen.getByText("Card 1")).toBeTruthy();
		await showAndGrade(/^Good · /);
		expect(grades(platform).at(-1)?.body).toEqual({ rating: 3, version: 2 });
		expect(screen.getByText("Card 2")).toBeTruthy();
	});

	it("keeps the card after a network failure, so the grade can be tried again", async () => {
		const { platform } = platformWith(2);
		await open(platform);
		platform.fail("POST", /\/grades$/);

		await showAndGrade(/^Good · /);

		expect(screen.getByRole("alert").textContent).toContain(
			"Review could not be reached",
		);
		expect(screen.getByText("Card 1")).toBeTruthy();
		expect(screen.getByText("Answer 1")).toBeTruthy();
		const good = screen.getByRole("button", { name: /^Good · / });
		expect((good as HTMLButtonElement).disabled).toBe(false);

		fireEvent.click(good);
		await settle();

		expect(grades(platform)).toHaveLength(2);
		expect(screen.getByText("Card 2")).toBeTruthy();
	});

	it("studies one deck or one topic from the address", async () => {
		const platform = fakePlatform();
		const compilers = platform.addDeck("Compilers");
		const databases = platform.addDeck("Databases");
		const due = platform.at(-HOUR);
		platform.addCard(compilers, { front: "LL(1)?", topics: [PARSING], due });
		platform.addCard(databases, { front: "Snapshot?", topics: [MVCC], due });

		await open(platform, `/review/study?deck=${databases.id}`);
		expect(screen.getByText("Snapshot?")).toBeTruthy();
		expect(screen.getByText("1 of 1")).toBeTruthy();
		cleanup();

		await open(platform, "/review/study?topic=parsing");
		expect(screen.getByText("LL(1)?")).toBeTruthy();

		expect(
			platform.callsTo("GET", "/review/api/queue").map(({ path }) => path),
		).toEqual([
			`/review/api/queue?deck=${databases.id}`,
			"/review/api/queue?topic=parsing",
		]);
	});

	it("links the card's source under the back", async () => {
		const platform = fakePlatform();
		const deck = platform.addDeck("Compilers");
		platform.addCard(deck, {
			front: "LL(1)?",
			topics: [PARSING],
			due: platform.at(-HOUR),
			source: { path: "/desk/sessions/42", title: "Compilers lecture 4" },
		});
		await open(platform);

		press(" ");

		expect(
			screen
				.getByRole("link", { name: "Compilers lecture 4" })
				.getAttribute("href"),
		).toBe("/desk/sessions/42");
	});

	it("says so when nothing is due at the start", async () => {
		const platform = fakePlatform();
		await open(platform);

		expect(screen.getByText("Nothing due")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Show answer" })).toBeNull();
	});
	it("opens from Start without leaving the page", async () => {
		const platform = fakePlatform();
		const compilers = platform.addDeck("Compilers");
		platform.addCard(compilers, {
			front: "LL(1)?",
			topics: [PARSING],
			due: platform.at(-HOUR),
		});
		openAt("/review", platform);
		const byDeck = await screen.findByRole("list", { name: "By deck" });

		fireEvent.click(within(byDeck).getByRole("link", { name: "Start" }));
		await loaded();

		expect(window.location.pathname + window.location.search).toBe(
			`/review/study?deck=${compilers.id}`,
		);
		expect(screen.getByText("LL(1)?")).toBeTruthy();
		expect(screen.getByRole("button", { name: "Show answer" })).toBeTruthy();
		expect(
			screen.getByRole("tab", { name: "Study" }).getAttribute("aria-selected"),
		).toBe("true");
	});
});
