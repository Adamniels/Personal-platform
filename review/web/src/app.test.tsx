import {
	cleanup,
	fireEvent,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { fakePlatform } from "./test/fake-platform";
import { openAt as openOver } from "./test/render";

/** The page at the path, for an owner with nothing in review yet. */
function openAt(path: string) {
	openOver(path, fakePlatform());
}

function selectedTab() {
	return screen
		.getAllByRole("tab")
		.find((tab) => tab.getAttribute("aria-selected") === "true");
}

afterEach(cleanup);

describe("the address decides the tab, and a tab changes the address", () => {
	test("the three tabs render, in order", () => {
		openAt("/review");

		expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual([
			"Study",
			"Cards",
			"Decks",
		]);
	});

	test.each([
		["/review", "Study", "Nothing to study yet"],
		["/review/study", "Study", "Nothing to study yet"],
		["/review/cards", "Cards", "Make a deck first"],
		["/review/decks", "Decks", "No decks yet"],
		["/review/cards/", "Cards", "Make a deck first"],
	])("%s selects %s", async (path, tab, empty) => {
		openAt(path);

		expect(selectedTab()?.textContent).toBe(tab);
		expect(
			await within(screen.getByRole("tabpanel")).findByText(empty),
		).toBeTruthy();
	});

	test("a tab changes the address, and Back returns to the previous one", async () => {
		openAt("/review/decks");

		fireEvent.click(screen.getByRole("tab", { name: "Cards" }));

		expect(window.location.pathname).toBe("/review/cards");
		expect(selectedTab()?.textContent).toBe("Cards");

		fireEvent.click(screen.getByRole("tab", { name: "Study" }));

		expect(window.location.pathname).toBe("/review");
		expect(selectedTab()?.textContent).toBe("Study");

		window.history.back();

		await waitFor(() => expect(window.location.pathname).toBe("/review/cards"));
		await waitFor(() => expect(selectedTab()?.textContent).toBe("Cards"));

		window.history.back();

		await waitFor(() => expect(window.location.pathname).toBe("/review/decks"));
		await waitFor(() => expect(selectedTab()?.textContent).toBe("Decks"));
	});

	test.each(["/review/nonsense", "/review/cards/deeper"])(
		"an unknown section, %s, shows the not found state",
		(path) => {
			openAt(path);

			expect(selectedTab()).toBeUndefined();
			expect(screen.queryByRole("tabpanel")).toBeNull();
			expect(screen.getByText("There is no such section")).toBeTruthy();
			const back = screen.getByRole("link", { name: "Go to Study" });
			expect(back.getAttribute("href")).toBe("/review");
		},
	);

	test("the page names itself", () => {
		openAt("/review");

		expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(
			"Review",
		);
		expect(
			screen.getByText(
				"Cards that come back to you, so what you mark as worth remembering sticks.",
			),
		).toBeTruthy();
	});
});
