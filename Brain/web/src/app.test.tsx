import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { createClient } from "./api/client";
import { App } from "./app";
import { Providers } from "./providers";
import { fakeBrain } from "./test/fake-brain";

function openAt(path: string) {
	window.history.pushState(null, "", path);
	render(
		<Providers client={createClient(fakeBrain().env)}>
			<App />
		</Providers>,
	);
}

function selectedTab() {
	return screen
		.getAllByRole("tab")
		.find((tab) => tab.getAttribute("aria-selected") === "true");
}

afterEach(cleanup);

describe("the address decides the section, and a tab changes the address", () => {
	test("/brain shows Profile", async () => {
		openAt("/brain");

		expect(selectedTab()?.textContent).toBe("Profile");
		expect(
			await within(screen.getByRole("tabpanel")).findByRole("article", {
				name: "Identity",
			}),
		).toBeTruthy();
	});

	test("/brain/knowledge shows Knowledge", async () => {
		openAt("/brain/knowledge");

		expect(selectedTab()?.textContent).toBe("Knowledge");
		expect(
			await within(screen.getByRole("tabpanel")).findByRole("button", {
				name: "Add topic",
			}),
		).toBeTruthy();
	});

	test("the eight sections are tabs, in order", () => {
		openAt("/brain");

		expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual([
			"Profile",
			"Rules",
			"Knowledge",
			"Now",
			"History",
			"Everything",
			"Timeline",
			"Ask",
		]);
	});

	test("an unknown section shows the not found state", () => {
		openAt("/brain/nonsense");

		expect(selectedTab()).toBeUndefined();
		expect(screen.queryByRole("tabpanel")).toBeNull();
		const notFound = screen.getByText("There is no such section");
		expect(notFound).toBeTruthy();
		const back = screen.getByRole("link", { name: "Go to Profile" });
		expect(back.getAttribute("href")).toBe("/brain");
	});

	test("a tab changes the address, and Back returns to the earlier section", async () => {
		openAt("/brain");

		fireEvent.click(screen.getByRole("tab", { name: "Rules" }));

		expect(window.location.pathname).toBe("/brain/rules");
		expect(selectedTab()?.textContent).toBe("Rules");
		expect(
			await within(screen.getByRole("tabpanel")).findByRole("button", {
				name: "Add rule",
			}),
		).toBeTruthy();

		window.history.back();

		await waitFor(() => expect(window.location.pathname).toBe("/brain"));
		await waitFor(() => expect(selectedTab()?.textContent).toBe("Profile"));
	});

	test("the page names itself", () => {
		openAt("/brain");

		expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(
			"Memory",
		);
		expect(
			screen.getByText(
				"What the platform knows about you. Correct anything that is wrong.",
			),
		).toBeTruthy();
	});
});
