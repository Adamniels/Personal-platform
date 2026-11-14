import {
	cleanup,
	fireEvent,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { type FakePlatform, fakePlatform } from "../test/fake-platform";
import { form, openAt, picker, settle, type } from "../test/render";

const KEY = "review-last-card";

let platform: FakePlatform;

beforeEach(() => {
	platform = fakePlatform();
	localStorage.clear();
});

afterEach(() => {
	cleanup();
	vi.restoreAllMocks();
});

async function openForm() {
	openAt("/review/cards", platform);
	fireEvent.click(await screen.findByRole("button", { name: "Add card" }));
	await settle();
	return form("New card");
}

function deckOf(add: HTMLElement) {
	return (
		within(add).getByRole("combobox", { name: "Deck" }) as HTMLSelectElement
	).value;
}

describe("the last deck and topics are offered on the next visit", () => {
	test("a card saved, the page left and opened again", async () => {
		platform.addDeck("Compilers");
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		platform.addTopic("Deadlines");
		const add = await openForm();
		type(within(add).getByRole("textbox", { name: "Front" }), "What is EDF?");
		type(within(add).getByRole("textbox", { name: "Back" }), "Earliest first");
		fireEvent.change(within(add).getByRole("combobox", { name: "Deck" }), {
			target: { value: deck.id },
		});
		for (const name of ["sched", "dead"]) {
			type(picker(add).field, name);
			fireEvent.keyDown(picker(add).field, { key: "Enter" });
		}
		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();
		cleanup();

		const again = await openForm();

		await waitFor(() =>
			expect(picker(again).picked()).toEqual(["Scheduling", "Deadlines"]),
		);
		expect(deckOf(again)).toBe(deck.id);
		expect(JSON.parse(localStorage.getItem(KEY) ?? "null")).toEqual({
			deck_id: deck.id,
			topics: [
				{ slug: "scheduling", name: "Scheduling" },
				{ slug: "deadlines", name: "Deadlines" },
			],
		});
	});
});

describe("what no longer exists is dropped silently", () => {
	test("a deck or a topic that is gone is not offered, and nothing says so", async () => {
		platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		localStorage.setItem(
			KEY,
			JSON.stringify({
				deck_id: crypto.randomUUID(),
				topics: [
					{ slug: "gone", name: "Gone" },
					{ slug: "scheduling", name: "Scheduling" },
				],
			}),
		);

		const add = await openForm();

		await waitFor(() => expect(picker(add).picked()).toEqual(["Scheduling"]));
		expect(deckOf(add)).toBe("");
		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("a topic that has since become an alias is offered as the topic it resolves to", async () => {
		platform.addDeck("Real Time Systems");
		const systems = platform.addTopic("Real Time Systems");
		platform.addTopic("RTS", { slug: "rts", aliasOf: systems });
		localStorage.setItem(
			KEY,
			JSON.stringify({ topics: [{ slug: "rts", name: "RTS" }] }),
		);

		const add = await openForm();

		await waitFor(() =>
			expect(picker(add).picked()).toEqual(["Real Time Systems"]),
		);
	});

	test("what is stored and cannot be read is ignored", async () => {
		const deck = platform.addDeck("Real Time Systems");
		localStorage.setItem(KEY, "{not json");

		const add = await openForm();
		expect(deckOf(add)).toBe("");

		cleanup();
		localStorage.setItem(
			KEY,
			JSON.stringify({ deck_id: deck.id, topics: "scheduling" }),
		);
		const shaped = await openForm();
		expect(deckOf(shaped)).toBe(deck.id);
		expect(picker(shaped).picked()).toEqual([]);
	});

	test("storage that is unavailable is ignored, and the form still saves", async () => {
		const deck = platform.addDeck("Real Time Systems");
		platform.addTopic("Scheduling");
		vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
			throw new DOMException("denied", "SecurityError");
		});
		vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
			throw new DOMException("denied", "SecurityError");
		});

		const add = await openForm();
		type(within(add).getByRole("textbox", { name: "Front" }), "What is EDF?");
		type(within(add).getByRole("textbox", { name: "Back" }), "Earliest first");
		fireEvent.change(within(add).getByRole("combobox", { name: "Deck" }), {
			target: { value: deck.id },
		});
		type(picker(add).field, "sched");
		fireEvent.keyDown(picker(add).field, { key: "Enter" });
		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(platform.callsTo("POST", "/review/api/cards")).toHaveLength(1);
		expect(screen.queryByRole("alert")).toBeNull();
	});
});
