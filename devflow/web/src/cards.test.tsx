import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import { type Card, CardsPanel, NO_CARDS, NOT_READ, stateWord } from "./cards";

afterEach(cleanup);

const ID = "0190a8f0-0000-7000-8000-000000000001";

const card = (number: number, state: string, reason: string | null): Card => ({
	number,
	title: `Card ${number}`,
	web_url: `https://github.com/Adamniels/Personal-platform/issues/${number}`,
	state,
	reason,
	waits_for: [],
	priority: "none",
	changed_at: "2026-11-17T12:00:00+00:00",
});

function renderPanel(load: (id: string) => Promise<{ cards: Card[] }>) {
	const loadCards = vi.fn(load);
	render(<CardsPanel id={ID} loadCards={loadCards} />);
	return loadCards;
}

const panel = async () =>
	within(await screen.findByRole("article", { name: "Cards" }));

describe("the cards panel", () => {
	test("lists each card with its number, title, state and reason, as the answer orders them", async () => {
		const loadCards = renderPanel(() =>
			Promise.resolve({
				cards: [
					card(12, "queued", null),
					card(13, "held", "Held by the label devflow-hold"),
					card(14, "queued", "Waits for #12"),
					card(
						15,
						"ignored",
						"Written by octo-stranger: only your own cards are picked",
					),
				],
			}),
		);
		const cards = await panel();

		expect(loadCards).toHaveBeenCalledWith(ID);
		const rows = within(
			await cards.findByRole("list", { name: "Cards" }),
		).getAllByRole("listitem");
		expect(
			rows.map(
				(row) => within(row).getByRole("link", { name: /^#\d+$/ }).textContent,
			),
		).toEqual(["#12", "#13", "#14", "#15"]);
		const at = (index: number) => {
			const row = rows[index];
			if (row === undefined) throw new Error(`no row ${index}`);
			return within(row);
		};
		const [queued, held, waiting, ignored] = [at(0), at(1), at(2), at(3)];
		expect(queued.getByText("Card 12")).toBeTruthy();
		expect(queued.getByText("Queued")).toBeTruthy();
		expect(held.getByText("Held")).toBeTruthy();
		expect(held.getByText("Held by the label devflow-hold")).toBeTruthy();
		expect(waiting.getByText("Queued")).toBeTruthy();
		expect(waiting.getByText("Waits for #12")).toBeTruthy();
		expect(ignored.getByText("Ignored")).toBeTruthy();
		expect(
			ignored.getByText(
				"Written by octo-stranger: only your own cards are picked",
			),
		).toBeTruthy();

		// Each links to its issue on GitHub, in a new tab.
		const link = queued.getByRole("link", { name: "#12" });
		expect(link.getAttribute("href")).toBe(
			"https://github.com/Adamniels/Personal-platform/issues/12",
		);
		expect(link.getAttribute("target")).toBe("_blank");
		expect(link.getAttribute("rel")).toBe("noreferrer");

		// Its title leads to the card's own page, in Dev flow.
		const page = queued.getByRole("link", { name: "Card 12" });
		expect(page.getAttribute("href")).toBe(`/devflow/projects/${ID}/cards/12`);
		expect(page.getAttribute("target")).toBeNull();
	});

	test("an address that is not https is no link, and a title is shown as text", async () => {
		renderPanel(() =>
			Promise.resolve({
				cards: [
					{
						...card(16, "queued", null),
						title: "<img src=x onerror=alert(1)>",
						web_url: "javascript:alert(1)",
					},
				],
			}),
		);
		const cards = await panel();

		expect(await cards.findByText("<img src=x onerror=alert(1)>")).toBeTruthy();
		expect(cards.queryByRole("link", { name: "#16" })).toBeNull();
		expect(cards.getByText("#16")).toBeTruthy();
		// The card's own page is Dev flow's address, never the code host's.
		expect(
			cards
				.getByRole("link", { name: "<img src=x onerror=alert(1)>" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${ID}/cards/16`);
		expect(document.querySelector("img")).toBeNull();
	});

	test("says how a card is made when there is none", async () => {
		renderPanel(() => Promise.resolve({ cards: [] }));

		expect(await (await panel()).findByText(NO_CARDS)).toBeTruthy();
	});

	test("cards that could not be read say so", async () => {
		renderPanel(() => Promise.reject(new Error("down")));

		expect((await (await panel()).findByRole("alert")).textContent).toBe(
			NOT_READ,
		);
	});

	test("every state has a word of its own", () => {
		const states = [
			"queued",
			"building",
			"checking",
			"reviewing",
			"fixing",
			"waiting_for_owner",
			"done",
			"stopped",
			"held",
			"ignored",
		];
		const words = states.map((state) => stateWord(state).word);
		expect(new Set(words).size).toBe(states.length);
		expect(stateWord("waiting_for_owner")).toEqual({
			word: "Waiting for you",
			tone: "needs-you",
		});
		// A state a later Dev flow adds is shown as it is named.
		expect(stateWord("paused").word).toBe("paused");
	});
});
