// An in-memory stand in for review's deck and card routes and the brain's topic routes, behind the
// clients' fetch, so a test renders the real page over the real clients and reads which calls it
// sent. It keeps the rules the page relies on, as "What it serves" and "What a card is" in
// review/README.md have them: decks by name without letter case, a name unique in any letter case
// (409), a deck holding cards not deleted (409), cards newest first, narrowed by `?deck=`, a
// change from a version that is not the card's current one refused (409), and a rule broken a 422
// naming its field. The brain's topics are listed by slug, aliases included, and a new one is
// canonical, its slug made from its name, 409 when the user has it. With the brain down, every call
// to it fails as fetch does when nothing answers. Anything else answers 404.
import type { components as BrainComponents } from "../api/brain-schema";
import {
	type ClientEnvironment,
	type Clients,
	createClients,
} from "../api/client";
import type { components } from "../api/schema";

type Deck = components["schemas"]["Deck"];
type Card = components["schemas"]["Card"];
type CardTopic = components["schemas"]["CardTopic"];
export type Topic = BrainComponents["schemas"]["Topic"];

export type Call = { method: string; path: string; body: unknown };

/** What the next matching call answers instead of doing its work. */
type Refusal = {
	method: string;
	path: RegExp;
	status: number;
	body: unknown;
};

const SLUG = /^[a-z0-9][a-z0-9-]{0,79}$/;

function problem(status: number, detail: string, errors?: object): Response {
	return new Response(
		JSON.stringify({ title: titleOf(status), status, detail, errors }),
		{ status, headers: { "content-type": "application/problem+json" } },
	);
}

function titleOf(status: number): string {
	return (
		{ 404: "Not Found", 409: "Conflict", 422: "Unprocessable Entity" }[
			status
		] ?? "Error"
	);
}

function json(status: number, body: unknown): Response {
	return new Response(JSON.stringify(body), {
		status,
		headers: { "content-type": "application/json" },
	});
}

export function slugOf(name: string): string {
	return name
		.toLowerCase()
		.replace(/[^a-z0-9]+/g, "-")
		.replace(/^-+|-+$/g, "")
		.slice(0, 80);
}

export function fakePlatform({ now = Date.UTC(2026, 10, 14, 9, 0) } = {}) {
	const decks: Deck[] = [];
	const cards: Card[] = [];
	const topics: Topic[] = [];
	const calls: Call[] = [];
	const refusals: Refusal[] = [];
	let brainDown = false;
	let minutes = 0;

	// Each row made a minute after the one before, so the order is never a tie.
	function stamp(): string {
		minutes += 1;
		return new Date(now + minutes * 60_000).toISOString();
	}

	function addDeck(name: string): Deck {
		const deck: Deck = {
			id: crypto.randomUUID(),
			name,
			card_count: 0,
			created_at: stamp(),
		};
		decks.push(deck);
		return deck;
	}

	function addTopic(
		name: string,
		{
			slug = slugOf(name),
			aliasOf,
			state = "canonical",
		}: { slug?: string; aliasOf?: Topic; state?: Topic["state"] } = {},
	): Topic {
		const id = crypto.randomUUID();
		const topic: Topic = {
			id,
			slug,
			name,
			state,
			alias_of: aliasOf?.id ?? null,
			resolves_to: aliasOf?.resolves_to ?? id,
			created_at: stamp(),
		};
		topics.push(topic);
		return topic;
	}

	function addCard(
		deck: Deck,
		{
			front,
			back = "An answer",
			topics: named,
			due,
		}: {
			front: string;
			back?: string;
			topics: readonly CardTopic[];
			due?: string;
		},
	): Card {
		const created = stamp();
		const card: Card = {
			id: crypto.randomUUID(),
			deck: { id: deck.id, name: deck.name },
			front,
			back,
			topics: [...named],
			source: null,
			state: "new",
			due: due ?? created,
			last_review: null,
			version: 1,
			created_at: created,
			updated_at: created,
		};
		cards.push(card);
		return card;
	}

	function countCards() {
		for (const deck of decks) {
			deck.card_count = cards.filter((card) => card.deck.id === deck.id).length;
		}
	}

	function deckRules(body: unknown, except?: string): Response | string {
		const name = String((body as { name?: unknown }).name ?? "").trim();
		if (name === "" || name.length > 100) {
			return problem(422, "A deck's name is 1 to 100 characters.", {
				name: ["A deck's name is 1 to 100 characters."],
			});
		}
		const taken = decks.some(
			(deck) =>
				deck.id !== except && deck.name.toLowerCase() === name.toLowerCase(),
		);
		if (taken) return problem(409, `You already have a deck named ${name}.`);
		return name;
	}

	type CardFields = {
		deck_id?: unknown;
		front?: unknown;
		back?: unknown;
		topics?: unknown;
	};

	/** The card as the fields would leave it, or the 422 they break. */
	function cardRules(
		fields: CardFields,
		current?: Card,
	): Response | Pick<Card, "deck" | "front" | "back" | "topics"> {
		const errors: Record<string, string[]> = {};
		const deckId = fields.deck_id ?? current?.deck.id;
		const deck = decks.find(({ id }) => id === deckId);
		if (!deck) errors.deck_id = ["The deck is not one of yours."];
		const text = (value: unknown, field: string) => {
			const trimmed = String(value ?? "").trim();
			if (trimmed === "") errors[field] = [`The ${field} is empty.`];
			return trimmed;
		};
		const front = text(fields.front ?? current?.front, "front");
		const back = text(fields.back ?? current?.back, "back");
		const named = (fields.topics ?? current?.topics ?? []) as CardTopic[];
		if (named.length < 1 || named.length > 5) {
			errors.topics = ["A card has one to five topics."];
		}
		named.forEach((topic, index) => {
			if (!SLUG.test(topic.slug)) {
				errors[`topics[${index}].slug`] = ["Not a topic's slug."];
			}
		});
		const first = Object.values(errors)[0]?.[0];
		if (first !== undefined || !deck) {
			return problem(422, first ?? "", errors);
		}
		return {
			deck: { id: deck.id, name: deck.name },
			front,
			back,
			topics: named.map(({ slug, name }) => ({ slug, name })),
		};
	}

	function review(method: string, path: string, body: unknown): Response {
		const [route = "", query = ""] = path.split("?");
		const search = new URLSearchParams(query);
		const deckId = /^\/decks\/([^/]+)$/.exec(route)?.[1];
		const cardId = /^\/cards\/([^/]+)$/.exec(route)?.[1];

		if (method === "GET" && route === "/decks") {
			countCards();
			return json(
				200,
				[...decks].sort((a, b) =>
					a.name.toLowerCase().localeCompare(b.name.toLowerCase()),
				),
			);
		}
		if (method === "POST" && route === "/decks") {
			const name = deckRules(body);
			if (name instanceof Response) return name;
			return json(201, addDeck(name));
		}
		if (deckId !== undefined) {
			const deck = decks.find(({ id }) => id === deckId);
			if (!deck) return problem(404, "There is no such deck.");
			if (method === "PATCH") {
				const name = deckRules(body, deck.id);
				if (name instanceof Response) return name;
				deck.name = name;
				for (const card of cards) {
					if (card.deck.id === deck.id) card.deck.name = name;
				}
				countCards();
				return json(200, deck);
			}
			if (method === "DELETE") {
				countCards();
				if (deck.card_count > 0) {
					return problem(409, "The deck holds cards.");
				}
				decks.splice(decks.indexOf(deck), 1);
				return new Response(null, { status: 204 });
			}
		}
		if (method === "GET" && route === "/cards") {
			const deck = search.get("deck");
			return json(
				200,
				[...cards]
					.reverse()
					.filter((card) => deck === null || card.deck.id === deck),
			);
		}
		if (method === "POST" && route === "/cards") {
			const made = cardRules(body as CardFields);
			if (made instanceof Response) return made;
			const deck = decks.find(({ id }) => id === made.deck.id) as Deck;
			const card = addCard(deck, made);
			return json(201, card);
		}
		if (cardId !== undefined) {
			const card = cards.find(({ id }) => id === cardId);
			if (!card) return problem(404, "There is no such card.");
			if (method === "PATCH") {
				const change = body as CardFields & { version?: unknown };
				if (change.version !== card.version) {
					return problem(409, "The card has changed since it was read.");
				}
				const made = cardRules(change, card);
				if (made instanceof Response) return made;
				Object.assign(card, made, {
					version: card.version + 1,
					updated_at: stamp(),
				});
				return json(200, card);
			}
			if (method === "DELETE") {
				cards.splice(cards.indexOf(card), 1);
				return new Response(null, { status: 204 });
			}
		}
		return problem(404, "Not found.");
	}

	function brain(method: string, path: string, body: unknown): Response {
		if (method === "GET" && path === "/topics") {
			return json(
				200,
				[...topics].sort((a, b) => a.slug.localeCompare(b.slug)),
			);
		}
		if (method === "POST" && path === "/topics") {
			const name = String((body as { name?: unknown }).name ?? "").trim();
			const slug = slugOf(name);
			if (topics.some((topic) => topic.slug === slug)) {
				return json(409, { detail: `You already have the topic ${slug}.` });
			}
			return json(201, addTopic(name, { slug }));
		}
		return json(404, { detail: "Not Found" });
	}

	const env: ClientEnvironment = {
		fetch: async (input, init) => {
			const url = String(input);
			const method = init.method ?? "GET";
			const body =
				typeof init.body === "string" ? JSON.parse(init.body) : undefined;
			calls.push({ method, path: url, body });
			const refusal = refusals.findIndex(
				(each) => each.method === method && each.path.test(url),
			);
			if (refusal !== -1) {
				const [{ status, body: answer }] = refusals.splice(refusal, 1) as [
					Refusal,
				];
				return json(status, answer);
			}
			if (url.startsWith("/review/api/")) {
				return review(method, url.slice("/review/api".length), body);
			}
			if (url.startsWith("/brain/api/")) {
				if (brainDown) throw new TypeError("Failed to fetch");
				return brain(method, url.slice("/brain/api".length), body);
			}
			throw new Error(`unexpected call to ${url}`);
		},
		location: {
			pathname: "/review",
			search: "",
			assign: () => undefined,
		},
	};

	return {
		decks,
		cards,
		topics,
		calls,
		addDeck,
		addTopic,
		addCard,
		/** The next call by the method to a path the pattern matches answers
		 * with this status and body. */
		refuse(method: string, path: RegExp, status: number, body: unknown) {
			refusals.push({ method, path, status, body });
		},
		/** Whether the brain answers at all. */
		setBrainDown(down: boolean) {
			brainDown = down;
		},
		/** The calls made by a method to paths starting with the prefix. */
		callsTo(method: string, prefix: string): Call[] {
			return calls.filter(
				(call) => call.method === method && call.path.startsWith(prefix),
			);
		},
		clients(): Clients {
			return createClients(env);
		},
	};
}

export type FakePlatform = ReturnType<typeof fakePlatform>;
