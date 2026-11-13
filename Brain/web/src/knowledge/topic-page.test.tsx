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

function openTopic(brain: ReturnType<typeof fakeBrain>, slug: string) {
	window.history.pushState(null, "", `/brain/knowledge/${slug}`);
	render(
		<Providers client={createClient(brain.env)}>
			<App />
		</Providers>,
	);
	return within(screen.getByRole("tabpanel", { name: "Knowledge" }));
}

/** The names a card lists, by its title. */
function listed(page: ReturnType<typeof openTopic>, title: string) {
	return within(page.getByRole("article", { name: title }))
		.queryAllByRole("link")
		.map((link) => link.textContent);
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

describe("a provisional topic's page", () => {
	test("offers Promote and no Edit, and Edit once it is promoted", async () => {
		const brain = fakeBrain();
		const indexing = brain.topic("Indexing", { state: "provisional" });
		const page = openTopic(brain, "indexing");

		const promote = await page.findByRole("button", { name: "Promote" });
		expect(page.queryByRole("button", { name: "Edit" })).toBeNull();
		expect(
			page.getByText("Promote it to give it a level and a focus."),
		).toBeTruthy();

		fireEvent.click(promote);

		expect(await page.findByRole("button", { name: "Edit" })).toBeTruthy();
		expect(page.queryByRole("button", { name: "Promote" })).toBeNull();
		expect(brain.changes()).toEqual([
			{
				method: "POST",
				path: `/brain/api/topics/${indexing.id}/promote`,
				body: undefined,
			},
		]);
	});
});

describe("Under and Includes are read through aliases", () => {
	test("a relation given on an alias shows on its target's page, and on the other end's", async () => {
		const brain = fakeBrain();
		const postgres = brain.topic("Postgres");
		const databases = brain.topic("Databases");
		const db = brain.topic("DB");
		brain.alias(db, databases);
		brain.placeUnder(postgres, db);

		const page = openTopic(brain, "postgres");
		await waitFor(() => expect(listed(page, "Under")).toEqual(["Databases"]));
		cleanup();

		const target = openTopic(brain, "databases");
		await waitFor(() =>
			expect(listed(target, "Includes")).toEqual(["Postgres"]),
		);
	});
});
