import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { createClient } from "../api/client";
import { App } from "../app";
import { Providers } from "../providers";
import { fakeBrain } from "../test/fake-brain";

afterEach(cleanup);

function openRules(brain: ReturnType<typeof fakeBrain>) {
	window.history.pushState(null, "", "/brain/rules");
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
	return within(screen.getByRole("tabpanel", { name: "Rules" }));
}

function texts(items: HTMLElement[]) {
	return items.map((item) => item.querySelector("p")?.textContent);
}

describe("the rules section lists the active rules in the order they were made", () => {
	test("oldest first, a correction in its place, a retired rule left out", async () => {
		const brain = fakeBrain();
		const first = brain.add("rule", "Answer in English");
		brain.add("rule", "Keep answers short");
		const gone = brain.add("rule", "Use metric units");
		brain.edit(first, "Answer in English, always");
		gone.status = "inactive";
		gone.inactive_reason = "retired";
		const rules = openRules(brain);

		await rules.findByRole("list");
		expect(texts(rules.getAllByRole("listitem"))).toEqual([
			"Answer in English, always",
			"Keep answers short",
		]);
		expect(
			rules.getByText(
				"How the platform and its agents should work with you. A rule applies everywhere, at once.",
			),
		).toBeTruthy();
	});

	test("with none, it says so", async () => {
		const rules = openRules(fakeBrain());

		expect(await rules.findByText("No rules yet")).toBeTruthy();
		expect(rules.queryByRole("listitem")).toBeNull();
	});
});

describe("adding a rule", () => {
	test("needs text, and is sent as a rule with no category", async () => {
		const brain = fakeBrain();
		const rules = openRules(brain);

		fireEvent.click(await rules.findByRole("button", { name: "Add rule" }));
		const form = within(rules.getByRole("form", { name: "Add rule" }));
		fireEvent.click(form.getByRole("button", { name: "Save" }));
		expect(form.getByText("Error: Write the rule first")).toBeTruthy();
		expect(brain.changes()).toEqual([]);

		fireEvent.change(form.getByLabelText("New rule"), {
			target: { value: " Answer in English " },
		});
		fireEvent.click(form.getByRole("button", { name: "Save" }));

		expect(await rules.findByText("Answer in English")).toBeTruthy();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: "/brain/api/items",
				body: { kind: "rule", content: "Answer in English" },
			},
		]);
		await waitFor(() =>
			expect(rules.queryByRole("form", { name: "Add rule" })).toBeNull(),
		);
	});
});

describe("correcting a rule", () => {
	test("sends only its kind and text, and the same text sends nothing", async () => {
		const brain = fakeBrain();
		const rule = brain.add("rule", "Answer in English");
		const rules = openRules(brain);

		fireEvent.click(await rules.findByRole("button", { name: "Edit" }));
		const field = rules.getByLabelText("Rule") as HTMLTextAreaElement;
		expect(field.value).toBe("Answer in English");
		fireEvent.click(rules.getByRole("button", { name: "Save" }));
		await waitFor(() => expect(rules.queryByLabelText("Rule")).toBeNull());
		expect(brain.changes()).toEqual([]);

		fireEvent.click(rules.getByRole("button", { name: "Edit" }));
		fireEvent.change(rules.getByLabelText("Rule"), {
			target: { value: "Answer in English, always" },
		});
		fireEvent.click(rules.getByRole("button", { name: "Save" }));

		expect(await rules.findByText("Answer in English, always")).toBeTruthy();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: `/brain/api/items/${rule.id}/edit`,
				body: { kind: "rule", content: "Answer in English, always" },
			},
		]);
	});
});
