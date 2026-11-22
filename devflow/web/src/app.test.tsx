import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { App } from "./app";

const PROJECT = {
	id: "0190a8f0-0000-7000-8000-000000000001",
	installation_id: 41,
	repository_id: 9001,
	owner: "Adamniels",
	name: "Personal-platform",
	created_at: "2026-11-16T10:00:00+00:00",
};

function openAt(path: string) {
	window.history.pushState(null, "", path);
	// The frame alone: the banner's own tests are in banner.test.tsx.
	render(
		<App
			loadStatus={() => new Promise(() => {})}
			project={{
				loadProjects: () => Promise.resolve([PROJECT]),
				loadWorkers: () =>
					Promise.resolve({
						key_made_at: null,
						recipe_version: null,
						start_command: "docker run",
						workers: [],
						held: null,
					}),
				makeKey: () => new Promise(() => {}),
				loadSettings: () => new Promise(() => {}),
				saveSettings: () => new Promise(() => {}),
				loadCards: () => new Promise(() => {}),
			}}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
				disconnect: () => Promise.resolve(),
			}}
			waiting={{
				loadWaiting: () =>
					Promise.resolve({
						count: 0,
						needs_relink: false,
						stopped_cards: [],
						refused_cards: [],
						question_cards: [],
						failing_projects: [],
						features: [],
						items: [],
						projects: [],
						own_items: [],
						cards: [],
						decisions: [],
					}),
				refresh: () => Promise.resolve(),
			}}
		/>,
	);
}

function selectedTab() {
	return screen
		.getAllByRole("tab")
		.find((tab) => tab.getAttribute("aria-selected") === "true");
}

afterEach(cleanup);

describe("the address decides the tab, and a tab changes the address", () => {
	test("the two tabs render, in order", () => {
		openAt("/devflow");

		expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual([
			"Waiting for you",
			"Projects",
		]);
	});

	test.each([
		[
			"/devflow",
			"Waiting for you",
			"Nothing waits for you",
			"Connect a repository under Projects to see what waits for you.",
		],
		[
			"/devflow/",
			"Waiting for you",
			"Nothing waits for you",
			"Connect a repository under Projects to see what waits for you.",
		],
		[
			"/devflow/projects",
			"Projects",
			"No project connected yet",
			"Connect a repository to see what waits for you in it.",
		],
		[
			"/devflow/projects/",
			"Projects",
			"No project connected yet",
			"Connect a repository to see what waits for you in it.",
		],
	])("%s selects %s", async (path, tab, empty, detail) => {
		openAt(path);

		expect(selectedTab()?.textContent).toBe(tab);
		const panel = within(screen.getByRole("tabpanel"));
		expect(await panel.findByText(empty)).toBeTruthy();
		expect(panel.getByText(detail)).toBeTruthy();
	});

	test("a tab changes the address, and Back returns to the previous one", async () => {
		openAt("/devflow");

		fireEvent.click(screen.getByRole("tab", { name: "Projects" }));

		expect(window.location.pathname).toBe("/devflow/projects");
		expect(selectedTab()?.textContent).toBe("Projects");

		fireEvent.click(screen.getByRole("tab", { name: "Waiting for you" }));

		expect(window.location.pathname).toBe("/devflow");
		expect(selectedTab()?.textContent).toBe("Waiting for you");

		window.history.back();

		await waitFor(() =>
			expect(window.location.pathname).toBe("/devflow/projects"),
		);
		await waitFor(() => expect(selectedTab()?.textContent).toBe("Projects"));

		window.history.back();

		await waitFor(() => expect(window.location.pathname).toBe("/devflow"));
		await waitFor(() =>
			expect(selectedTab()?.textContent).toBe("Waiting for you"),
		);
	});

	test.each(["/devflow/nonsense", "/devflow/projects/deeper"])(
		"an unknown section, %s, shows the not found state",
		(path) => {
			openAt(path);

			expect(selectedTab()).toBeUndefined();
			expect(screen.queryByRole("tabpanel")).toBeNull();
			expect(screen.getByText("There is no such section")).toBeTruthy();
			const back = screen.getByRole("link", { name: "Go to Waiting for you" });
			expect(back.getAttribute("href")).toBe("/devflow");
		},
	);

	test.each([
		"/devflow/projects/0190a8f0-0000-7000-8000-000000000001",
		"/devflow/projects/0190A8F0-0000-7000-8000-000000000001/",
	])("a project's address, %s, opens its page under Projects", async (path) => {
		openAt(path);

		expect(selectedTab()?.textContent).toBe("Projects");
		const panel = within(screen.getByRole("tabpanel"));
		expect(
			await panel.findByRole("heading", {
				name: "Adamniels/Personal-platform",
			}),
		).toBeTruthy();
		expect(panel.getByRole("article", { name: "Workers" })).toBeTruthy();
		expect(panel.queryByText("No project connected yet")).toBeNull();
	});

	test("the way back from a project's page goes to Projects", async () => {
		openAt("/devflow/projects/0190a8f0-0000-7000-8000-000000000001");

		fireEvent.click(
			await screen.findByRole("link", { name: "Back to Projects" }),
		);

		expect(window.location.pathname).toBe("/devflow/projects");
		expect(await screen.findByText("No project connected yet")).toBeTruthy();
	});

	test("the page names itself", () => {
		openAt("/devflow");

		expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(
			"Dev flow",
		);
		expect(
			screen.getByText("What your agents built, and what waits for you."),
		).toBeTruthy();
		expect(
			screen.getByRole("tablist", { name: "Dev flow sections" }),
		).toBeTruthy();
	});
});

describe("the top bar names the tab", () => {
	afterEach(() => {
		for (const nav of document.querySelectorAll("platform-nav")) nav.remove();
	});

	function crumb() {
		return document.querySelector("platform-nav")?.getAttribute("crumb");
	}

	function withNav() {
		document.body.append(document.createElement("platform-nav"));
	}

	test("the crumb is Dev flow and the tab, and follows a tab", async () => {
		withNav();
		openAt("/devflow");

		expect(crumb()).toBe("Dev flow / Waiting for you");
		fireEvent.click(screen.getByRole("tab", { name: "Projects" }));
		await waitFor(() => expect(crumb()).toBe("Dev flow / Projects"));
	});

	test("an unknown address leaves the bar to name Dev flow", () => {
		withNav();
		document.querySelector("platform-nav")?.setAttribute("crumb", "Old");
		openAt("/devflow/nothing");

		expect(document.querySelector("platform-nav")?.hasAttribute("crumb")).toBe(
			false,
		);
	});
});
