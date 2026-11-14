import {
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import axe from "axe-core";
import { useId, useState } from "react";
import { afterEach, beforeEach, describe, expect, test } from "vitest";
import { Providers } from "../providers";
import { type FakePlatform, fakePlatform } from "../test/fake-platform";
import {
	descriptionOf,
	form,
	openAt,
	picker,
	settle,
	type,
} from "../test/render";
import { TopicPicker } from "./topic-picker";
import type { PickedTopic } from "./topics";

let platform: FakePlatform;

beforeEach(() => {
	platform = fakePlatform();
	localStorage.clear();
});

afterEach(cleanup);

/** The picker alone, in a form of a test's own, over the fake brain, once
 * the topics have loaded. The card form's use of it is in
 * src/cards/card-form.test.tsx and at the end of this file. */
function Harness() {
	const [value, setValue] = useState<PickedTopic[]>([]);
	const unavailable = useId();
	return (
		<main>
			<form aria-label="Card">
				<TopicPicker
					value={value}
					onChange={setValue}
					unavailableId={unavailable}
				/>
			</form>
		</main>
	);
}

async function openForm() {
	render(
		<Providers clients={platform.clients()}>
			<Harness />
		</Providers>,
	);
	await settle();
	return form("Card");
}

/** A brain with a topic, another, and an alias of the first. */
function someTopics() {
	const systems = platform.addTopic("Real Time Systems");
	platform.addTopic("Scheduling", { slug: "sched" });
	platform.addTopic("RTS", { slug: "rts", aliasOf: systems });
	return systems;
}

describe("typing filters the owner's topics", () => {
	test("by name and by slug, without letter case", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "SCHED");
		expect(topics.options()).toEqual(["Scheduling"]);

		type(topics.field, "time");
		expect(topics.options()).toEqual([
			"Real Time Systems",
			"Create topic time",
		]);
	});

	test("an alias is shown as the topic it resolves to, once", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "rts");

		expect(topics.options()).toEqual(["Real Time Systems"]);

		type(topics.field, "r");
		expect(
			topics.options().filter((name) => name === "Real Time Systems"),
		).toHaveLength(1);
	});

	test("the brain's topics are read once per opening of the form", async () => {
		someTopics();
		const add = await openForm();
		type(picker(add).field, "s");
		type(picker(add).field, "sc");
		await settle();

		expect(platform.callsTo("GET", "/brain/api/topics")).toHaveLength(1);
	});
});

describe("picking a topic", () => {
	test("Enter picks the active one, and the field empties", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "sched");
		fireEvent.keyDown(topics.field, { key: "Enter" });

		expect(topics.picked()).toEqual(["Scheduling"]);
		expect((topics.field as HTMLInputElement).value).toBe("");
		expect(topics.options()).toEqual([]);
	});

	test("Arrow Down moves the active option, and Enter picks it", async () => {
		platform.addTopic("Scheduling");
		platform.addTopic("Schedulability");
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "schedul");
		fireEvent.keyDown(topics.field, { key: "ArrowDown" });
		fireEvent.keyDown(topics.field, { key: "Enter" });

		expect(topics.picked()).toEqual(["Scheduling"]);
	});

	test("a click picks one", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "time");
		fireEvent.click(
			within(add).getByRole("option", { name: "Real Time Systems" }),
		);

		expect(topics.picked()).toEqual(["Real Time Systems"]);
	});

	test("a picked topic is not offered again", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);
		type(topics.field, "rts");
		fireEvent.keyDown(topics.field, { key: "Enter" });

		type(topics.field, "real");

		expect(topics.options()).toEqual(["Create topic real"]);
	});

	test("Remove <name> takes a topic away", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);
		type(topics.field, "sched");
		fireEvent.keyDown(topics.field, { key: "Enter" });

		fireEvent.click(
			within(add).getByRole("button", { name: "Remove Scheduling" }),
		);

		expect(topics.picked()).toEqual([]);
	});

	test("Backspace in the empty field removes the last", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);
		for (const text of ["sched", "time"]) {
			type(topics.field, text);
			fireEvent.keyDown(topics.field, { key: "Enter" });
		}

		type(topics.field, "x");
		fireEvent.keyDown(topics.field, { key: "Backspace" });
		expect(topics.picked()).toEqual(["Scheduling", "Real Time Systems"]);

		type(topics.field, "");
		fireEvent.keyDown(topics.field, { key: "Backspace" });
		expect(topics.picked()).toEqual(["Scheduling"]);
	});

	test("at most five", async () => {
		for (const name of ["One", "Two", "Three", "Four", "Five", "Six"]) {
			platform.addTopic(`Topic ${name}`);
		}
		const add = await openForm();
		const topics = picker(add);
		for (const name of ["One", "Two", "Three", "Four", "Five"]) {
			type(topics.field, name);
			fireEvent.keyDown(topics.field, { key: "Enter" });
		}
		expect(topics.picked()).toHaveLength(5);

		type(topics.field, "Six");
		fireEvent.keyDown(topics.field, { key: "Enter" });

		expect(topics.options()).toEqual([]);
		expect(topics.picked()).toHaveLength(5);
		expect(topics.field.getAttribute("aria-expanded")).toBe("false");
		expect(descriptionOf(topics.field)).toContain("Up to five.");
	});
});

describe("a name with no match offers to create it", () => {
	test("Create topic <name> posts it to the brain and picks the result", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "Distributed Systems");
		expect(topics.options()).toEqual(["Create topic Distributed Systems"]);
		fireEvent.keyDown(topics.field, { key: "Enter" });
		await settle();

		expect(platform.callsTo("POST", "/brain/api/topics")).toEqual([
			{
				method: "POST",
				path: "/brain/api/topics",
				body: { name: "Distributed Systems" },
			},
		]);
		expect(topics.picked()).toEqual(["Distributed Systems"]);

		// The new topic is offered from then on, without reading the list again.
		fireEvent.click(
			within(add).getByRole("button", { name: "Remove Distributed Systems" }),
		);
		type(topics.field, "distrib");
		expect(topics.options()).toEqual([
			"Distributed Systems",
			"Create topic distrib",
		]);
		expect(platform.callsTo("GET", "/brain/api/topics")).toHaveLength(1);
	});

	test("a name that makes the slug of a topic the owner has offers that topic, not creating it", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "Real-Time  Systems!");

		expect(topics.options()).toEqual(["Real Time Systems"]);
	});

	test("a name the owner has as a topic already is not offered to create", async () => {
		someTopics();
		const add = await openForm();
		const topics = picker(add);

		type(topics.field, "scheduling");

		expect(topics.options()).toEqual(["Scheduling"]);
	});

	test("a refusal from the brain shows on the field", async () => {
		const add = await openForm();
		const topics = picker(add);
		platform.refuse("POST", /\/brain\/api\/topics$/, 409, {
			detail: "You already have the topic distributed-systems.",
		});

		type(topics.field, "Distributed Systems");
		fireEvent.keyDown(topics.field, { key: "Enter" });
		await settle();

		expect(topics.picked()).toEqual([]);
		expect(descriptionOf(topics.field)).toContain(
			"Error: You already have the topic distributed-systems.",
		);
		expect(screen.queryByRole("alert")).toBeNull();
	});
});

describe("the brain unreachable", () => {
	test("the picker says topics are unavailable, and takes nothing", async () => {
		platform.setBrainDown(true);
		const add = await openForm();

		const message = "Topics are unavailable while the brain cannot be reached";
		// Waited for: the brain's refusal can land after the form first settles.
		expect(await within(add).findByText(message)).toBeTruthy();
		const { field } = picker(add);
		expect((field as HTMLInputElement).disabled).toBe(true);
		expect(descriptionOf(field)).toContain(message);
		// The picker says it; no toast says it again.
		expect(screen.queryByRole("alert")).toBeNull();
	});
});

describe("the ARIA combobox pattern", () => {
	test("a combobox that controls a listbox, its active option named", async () => {
		someTopics();
		const add = await openForm();
		const { field } = picker(add);
		expect(field.getAttribute("aria-expanded")).toBe("false");
		expect(field.getAttribute("aria-autocomplete")).toBe("list");

		type(field, "s");
		const listbox = within(add).getByRole("listbox", { name: "Topics" });
		expect(field.getAttribute("aria-expanded")).toBe("true");
		expect(field.getAttribute("aria-controls")).toBe(listbox.id);
		const options = within(listbox).getAllByRole("option");
		expect(field.getAttribute("aria-activedescendant")).toBe(options[0]?.id);
		expect(options[0]?.getAttribute("aria-selected")).toBe("true");

		fireEvent.keyDown(field, { key: "ArrowDown" });
		expect(field.getAttribute("aria-activedescendant")).toBe(options[1]?.id);
		fireEvent.keyDown(field, { key: "ArrowUp" });
		expect(field.getAttribute("aria-activedescendant")).toBe(options[0]?.id);

		fireEvent.keyDown(field, { key: "Escape" });
		expect(field.getAttribute("aria-expanded")).toBe("false");
		expect(within(add).queryByRole("listbox")).toBeNull();
	});

	test("axe finds nothing wrong with the form, the list open and a topic picked", async () => {
		someTopics();
		const add = await openForm();
		const { field } = picker(add);
		type(field, "sched");
		fireEvent.keyDown(field, { key: "Enter" });
		type(field, "s");

		const result = await axe.run(add, {
			// jsdom lays nothing out, so contrast cannot be computed here; the
			// tokens' contrast is design/ui's own test.
			rules: { "color-contrast": { enabled: false } },
		});

		expect(
			result.violations.map(({ id, nodes }) => ({
				id,
				targets: nodes.map((node) => node.target.join(" ")),
			})),
		).toEqual([]);
	});
});

describe("in the card form", () => {
	async function openCardForm() {
		openAt("/review/cards", platform);
		fireEvent.click(await screen.findByRole("button", { name: "Add card" }));
		await settle();
		return form("New card");
	}

	test("picking an alias saves the topic it resolves to", async () => {
		const deck = platform.addDeck("Real Time Systems");
		const systems = someTopics();
		const add = await openCardForm();
		type(within(add).getByRole("textbox", { name: "Front" }), "What is RTS?");
		type(within(add).getByRole("textbox", { name: "Back" }), "Real time");
		fireEvent.change(within(add).getByRole("combobox", { name: "Deck" }), {
			target: { value: deck.id },
		});
		const topics = picker(add);
		type(topics.field, "rts");
		fireEvent.keyDown(topics.field, { key: "Enter" });
		fireEvent.click(within(add).getByRole("button", { name: "Save" }));
		await settle();

		expect(
			platform
				.callsTo("POST", "/review/api/cards")
				.map((call) => (call.body as { topics: unknown }).topics),
		).toEqual([[{ slug: systems.slug, name: systems.name }]]);
	});

	test("the brain unreachable, the card cannot be saved, and the rest of the page works", async () => {
		platform.addDeck("Real Time Systems");
		platform.setBrainDown(true);
		const add = await openCardForm();

		const message = "Topics are unavailable while the brain cannot be reached";
		// Waited for: the brain's refusal can land after the form first settles.
		expect(await within(add).findByText(message)).toBeTruthy();
		const save = within(add).getByRole("button", {
			name: "Save",
		}) as HTMLButtonElement;
		expect(save.disabled).toBe(true);
		expect(descriptionOf(save)).toBe(message);
		expect(screen.queryByRole("alert")).toBeNull();

		fireEvent.click(screen.getByRole("tab", { name: "Decks" }));
		expect(
			await within(
				await screen.findByRole("list", { name: "Decks" }),
			).findByText("Real Time Systems"),
		).toBeTruthy();
	});
});
