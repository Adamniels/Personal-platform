import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import {
	type Card,
	CardsPanel,
	type Feature,
	NO_CARDS,
	NOT_READ,
	stateWord,
} from "./cards";

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

const feature = (
	number: number,
	state: string,
	reason: string | null,
	cards: Card[],
): Feature => ({
	number,
	title: `Feature ${number}`,
	web_url: `https://github.com/Adamniels/Personal-platform/issues/${number}`,
	state,
	reason,
	priority: "none",
	changed_at: "2026-11-19T12:00:00+00:00",
	cards,
});

function renderPanel(
	load: (id: string) => Promise<{ cards: Card[]; features: Feature[] }>,
) {
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
				features: [],
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
				features: [],
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
		renderPanel(() => Promise.resolve({ cards: [], features: [] }));

		expect(await (await panel()).findByText(NO_CARDS)).toBeTruthy();
	});

	test("lists each feature above the cards on their own, with its cards in order and their states", async () => {
		renderPanel(() =>
			Promise.resolve({
				features: [
					feature(40, "held", "Held by the label devflow-hold", [
						card(43, "held", "The feature is held by the label devflow-hold"),
						card(41, "held", "The feature is held by the label devflow-hold"),
					]),
					feature(30, "queued", null, [card(31, "queued", "Waits for #9")]),
				],
				cards: [card(50, "queued", null)],
			}),
		);
		const cards = await panel();

		const features = await cards.findByRole("list", { name: "Features" });
		const items = [...features.children] as HTMLElement[];
		expect(
			items.map((item) => within(item).getAllByRole("link")[0]?.textContent),
		).toEqual(["#40", "#30"]);
		const [held, queued] = items.map((item) => within(item));
		if (held === undefined || queued === undefined)
			throw new Error("two features");

		// The feature's number links to its issue; its title is text until the
		// feature has a page of its own.
		expect(held.getByRole("link", { name: "#40" }).getAttribute("href")).toBe(
			"https://github.com/Adamniels/Personal-platform/issues/40",
		);
		expect(held.getByText("Feature 40").closest("a")).toBeNull();
		expect(held.getAllByText("Held").length).toBe(3);
		expect(held.getByText("Held by the label devflow-hold")).toBeTruthy();

		// Its cards in the order given, each a link to its own page.
		const own = held.getByRole("list", { name: "Cards of #40" });
		expect(
			within(own)
				.getAllByRole("listitem")
				.map(
					(row) =>
						within(row).getByRole("link", { name: /^#\d+$/ }).textContent,
				),
		).toEqual(["#43", "#41"]);
		expect(
			within(own).getByRole("link", { name: "Card 43" }).getAttribute("href"),
		).toBe(`/devflow/projects/${ID}/cards/43`);
		expect(queued.getByText("Waits for #9")).toBeTruthy();

		// The card on its own is in the list of cards, and only it.
		const alone = within(cards.getByRole("list", { name: "Cards" }));
		expect(alone.getAllByRole("listitem").length).toBe(1);
		expect(alone.getByText("Card 50")).toBeTruthy();
	});

	test("a feature alone needs no list of cards on their own", async () => {
		renderPanel(() =>
			Promise.resolve({
				features: [feature(40, "queued", null, [card(41, "queued", null)])],
				cards: [],
			}),
		);
		const cards = await panel();

		expect(await cards.findByRole("list", { name: "Features" })).toBeTruthy();
		expect(cards.queryByRole("list", { name: "Cards" })).toBeNull();
		expect(cards.queryByText(NO_CARDS)).toBeNull();
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
