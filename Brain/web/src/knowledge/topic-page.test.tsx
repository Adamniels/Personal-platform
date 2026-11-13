import {
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { createClient } from "../api/client";
import { App } from "../app";
import { Providers } from "../providers";
import { fakeBrain } from "../test/fake-brain";

afterEach(cleanup);

function openTopic(brain: ReturnType<typeof fakeBrain>, slug: string) {
	window.history.pushState(null, "", `/brain/knowledge/${slug}`);
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
	return within(screen.getByRole("tabpanel", { name: "Knowledge" }));
}

describe("Place under… offers only canonical topics", () => {
	test("not a provisional topic, not an alias, not the topic itself", async () => {
		const brain = fakeBrain();
		brain.topic("Postgres");
		const databases = brain.topic("Databases");
		brain.topic("Storage");
		brain.topic("Indexing", { state: "provisional" });
		brain.alias(brain.topic("DB"), databases);
		const page = openTopic(brain, "postgres");

		fireEvent.click(await page.findByRole("button", { name: "Place under…" }));
		const select = page.getByLabelText("Place under") as HTMLSelectElement;

		expect(
			Array.from(select.options)
				.filter(({ value }) => value)
				.map(({ text }) => text),
		).toEqual(["Databases", "Storage"]);
	});
});
