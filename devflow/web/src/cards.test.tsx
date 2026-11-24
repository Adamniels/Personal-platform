import {
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import {
	type Card,
	CardsPanel,
	CardsTable,
	type Feature,
	FeatureCard,
	NO_CARDS,
	NOT_READ,
	stateWord,
} from "./cards";
import css from "./cards.module.css?raw";

afterEach(cleanup);

const ID = "0190a8f0-0000-7000-8000-000000000001";
const NOW = Date.parse("2026-11-17T12:00:00Z");

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
						"octo-stranger is not a trusted account of this project",
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
				"octo-stranger is not a trusted account of this project",
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

		// The feature's number links to its issue, and its title to the
		// feature's own page, in Dev flow.
		expect(held.getByRole("link", { name: "#40" }).getAttribute("href")).toBe(
			"https://github.com/Adamniels/Personal-platform/issues/40",
		);
		const page = held.getByRole("link", { name: "Feature 40" });
		expect(page.getAttribute("href")).toBe(
			`/devflow/projects/${ID}/features/40`,
		);
		expect(page.getAttribute("target")).toBeNull();
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
			"reading",
			"building",
			"checking",
			"reviewing",
			"fixing",
			"waiting_for_owner",
			"done",
			"stopped",
			"refused",
			"held",
			"ignored",
		];
		const words = states.map((state) => stateWord(state).word);
		expect(new Set(words).size).toBe(states.length);
		expect(stateWord("waiting_for_owner")).toEqual({
			word: "Waiting for you",
			tone: "needs-you",
		});
		// A card read before anything is built, and one read as too thin.
		expect(stateWord("reading")).toEqual({ word: "Reading", tone: "running" });
		expect(stateWord("refused")).toEqual({
			word: "Too thin to build",
			tone: "failed",
		});
		// A state a later Dev flow adds is shown as it is named.
		expect(stateWord("paused").word).toBe("paused");
	});

	test("a feature bringing the default branch in says so", () => {
		expect(stateWord("bringing_in")).toEqual({
			word: "Bringing in the default branch",
			tone: "running",
		});
	});

	test("a feature whose conflict an agent resolves says so", () => {
		expect(stateWord("resolving")).toEqual({
			word: "Resolving a conflict",
			tone: "running",
		});
	});
});

/** The rule a selector's block holds in the module's CSS, or "" without one. */
const rule = (selector: string): string => {
	const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
	return css.match(new RegExp(`${escaped}\\s*\\{([^}]*)\\}`))?.[1] ?? "";
};

describe("a project's cards, cards first", () => {
	test("a feature shows its steps and its cards in order with their states", () => {
		render(
			<FeatureCard
				project={ID}
				feature={feature(40, "building", null, [
					card(43, "done", null),
					card(41, "building", null),
					card(42, "queued", "Waits for #44"),
					card(45, "ignored", "octo-stranger is not a trusted account"),
				])}
			/>,
		);

		// A card of its own: its label with its state, and its title as the
		// hero label, a link to the feature's page.
		const article = screen.getByRole("article", { name: "Feature #40" });
		const feature40 = within(article);
		expect(feature40.getAllByText("Building")).toHaveLength(2);
		const title = feature40.getByRole("link", { name: "Feature 40" });
		expect(title.getAttribute("href")).toBe(
			`/devflow/projects/${ID}/features/40`,
		);
		expect(rule(".hero")).toMatch(/font-family:\s*var\(--font-heading\)/);
		expect(rule(".hero")).toMatch(/font-size:\s*var\(--text-hero-size\)/);
		// Its number still links to its parent issue on GitHub.
		expect(
			feature40.getByRole("link", { name: "#40" }).getAttribute("href"),
		).toBe("https://github.com/Adamniels/Personal-platform/issues/40");

		// Landed of total, an ignored card in neither, as a mono line and as
		// steps: one per card, landed in the accent, the current outlined, the
		// rest on --surface-3.
		expect(feature40.getByText("1 of 3 landed")).toBeTruthy();
		const steps = feature40.getByRole("img", { name: "1 of 3 landed" });
		expect(
			[...steps.children].map((step) => step.getAttribute("data-step")),
		).toEqual(["landed", "current", "to-do"]);
		expect(rule(".step")).toMatch(/background:\s*var\(--surface-3\)/);
		expect(rule('.step[data-step="landed"]')).toMatch(
			/background:\s*var\(--accent\)/,
		);
		expect(rule('.step[data-step="current"]')).toMatch(
			/background:\s*transparent/,
		);
		expect(rule('.step[data-step="current"]')).toMatch(
			/border:\s*1px solid var\(--accent-border\)/,
		);

		// Its cards in order, each a row with its number, its title linked to
		// its page, its state, and its reason under it.
		const rows = within(
			feature40.getByRole("list", { name: "Cards of #40" }),
		).getAllByRole("listitem");
		expect(
			rows.map(
				(row) => within(row).getByRole("link", { name: /^#\d+$/ }).textContent,
			),
		).toEqual(["#43", "#41", "#42", "#45"]);
		expect(
			rows.map(
				(row) =>
					within(row).getByText(/^(Done|Building|Queued|Ignored)$/).textContent,
			),
		).toEqual(["Done", "Building", "Queued", "Ignored"]);
		expect(
			within(rows[1] as HTMLElement)
				.getByRole("link", { name: "Card 41" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${ID}/cards/41`);
		expect(
			within(rows[2] as HTMLElement).getByText("Waits for #44"),
		).toBeTruthy();
	});

	test("a feature whose cards have all landed has no current step", () => {
		render(
			<FeatureCard
				project={ID}
				feature={feature(40, "done", null, [
					card(41, "done", null),
					card(42, "done", null),
				])}
			/>,
		);

		const steps = screen.getByRole("img", { name: "2 of 2 landed" });
		expect(
			[...steps.children].map((step) => step.getAttribute("data-step")),
		).toEqual(["landed", "landed"]);
	});

	test("cards on their own are a table newest change first, Changed hidden on phone", () => {
		render(
			<CardsTable
				project={ID}
				now={NOW}
				cards={[
					{
						...card(12, "queued", null),
						changed_at: "2026-11-17T09:00:00+00:00",
					},
					{
						...card(14, "held", "Held by the label devflow-hold"),
						changed_at: "2026-11-17T11:48:00+00:00",
					},
					{
						...card(13, "done", null),
						changed_at: "2026-11-15T12:00:00+00:00",
					},
				]}
			/>,
		);

		// A table in a card of its own, saying its order.
		const article = within(
			screen.getByRole("article", { name: "Cards on their own" }),
		);
		expect(article.getByText("newest change first")).toBeTruthy();
		const table = within(
			article.getByRole("table", { name: "Cards on their own" }),
		);
		const headers = table.getAllByRole("columnheader");
		expect(headers.map((header) => header.textContent)).toEqual([
			"#",
			"Card",
			"State",
			"Changed",
		]);
		// Changed is the sorted column, and the one hidden on phone.
		expect(headers.map((header) => header.getAttribute("aria-sort"))).toEqual([
			null,
			null,
			null,
			"descending",
		]);
		expect(
			headers.map((header) => header.getAttribute("data-priority")),
		).toEqual(["1", "1", "1", "3"]);

		const rows = table.getAllByRole("row").slice(1);
		const cells = (row: HTMLElement | undefined) =>
			within(row as HTMLElement).getAllByRole("cell");
		expect(rows.map((row) => cells(row)[0]?.textContent)).toEqual([
			"#14",
			"#12",
			"#13",
		]);
		// The card's title, a link to its page, with its reason under it.
		const [, titled, state, changed] = cells(rows[0]);
		expect(
			within(titled as HTMLElement)
				.getByRole("link", { name: "Card 14" })
				.getAttribute("href"),
		).toBe(`/devflow/projects/${ID}/cards/14`);
		expect(
			within(titled as HTMLElement).getByText("Held by the label devflow-hold"),
		).toBeTruthy();
		expect(state?.textContent).toBe("Held");
		// When it changed, from changed_at, short, in the mono font.
		expect(changed?.querySelector("time")?.getAttribute("dateTime")).toBe(
			"2026-11-17T11:48:00+00:00",
		);
		expect(rows.map((row) => cells(row)[3]?.textContent)).toEqual([
			"12m",
			"3h",
			"2d",
		]);

		// Pressing Changed turns the order round, and the caption with it.
		fireEvent.click(table.getByRole("button", { name: "Changed" }));
		expect(
			table
				.getAllByRole("row")
				.slice(1)
				.map((row) => cells(row)[0]?.textContent),
		).toEqual(["#13", "#12", "#14"]);
		expect(article.getByText("oldest change first")).toBeTruthy();
	});
});
