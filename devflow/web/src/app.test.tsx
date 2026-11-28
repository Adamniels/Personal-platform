import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { App, type PausesApi } from "./app";

const PROJECT = {
	id: "0190a8f0-0000-7000-8000-000000000001",
	installation_id: 41,
	repository_id: 9001,
	owner: "Adamniels",
	name: "Personal-platform",
	created_at: "2026-11-16T10:00:00+00:00",
	last_read_at: "2026-11-17T11:58:00+00:00",
	paused_at: null,
	summary: {
		cards: { needs_you: 0, in_progress: 0, queued: 0, done: 0 },
		workers: { count: 0, last_seen_at: null, held: null, cannot_run: null },
		setting_up: null,
		feature: null,
	},
};

/** Nothing paused, as a new user's answer. */
const RUNS: PausesApi = {
	loadPaused: () => Promise.resolve({ paused: false, paused_at: null }),
	pauseAll: () => Promise.resolve(),
};

function openAt(path: string, pauses: PausesApi = RUNS) {
	window.history.pushState(null, "", path);
	// The frame alone: the banner's own tests are in banner.test.tsx.
	render(
		<App
			loadStatus={() => new Promise(() => {})}
			pauses={pauses}
			usage={{ loadUsage: () => new Promise(() => {}) }}
			project={{
				loadProjects: () => Promise.resolve([PROJECT]),
				pauseProject: () => Promise.resolve(),
				stopNow: () => Promise.resolve(0),
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
				loadSetup: () => new Promise(() => {}),
				checkAgain: () => Promise.resolve(),
				mergeSetup: () => Promise.resolve(),
				runSetupAgain: () => Promise.resolve(),
				sendSetupBack: () => Promise.resolve(),
				loadValues: () => new Promise(() => {}),
				giveValue: () => Promise.resolve(),
				removeValue: () => Promise.resolve(),
				loadSchedules: () => new Promise(() => {}),
				saveSchedules: () => new Promise(() => {}),
				saveTimeZone: () => new Promise(() => {}),
			}}
			settings={{
				loadProjects: () => Promise.resolve([PROJECT]),
				loadSettings: () => new Promise(() => {}),
				saveSettings: () => new Promise(() => {}),
				loadValues: () => new Promise(() => {}),
				giveValue: () => Promise.resolve(),
				removeValue: () => Promise.resolve(),
				loadSchedules: () => new Promise(() => {}),
				saveSchedules: () => new Promise(() => {}),
				saveTimeZone: () => new Promise(() => {}),
				loadApiKey: () => new Promise(() => {}),
				disconnect: () => Promise.resolve(),
			}}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
			}}
			waiting={{
				loadWaiting: () =>
					Promise.resolve({
						count: 0,
						needs_relink: false,
						stopped_cards: [],
						stopped_features: [],
						refused_cards: [],
						question_cards: [],
						failing_projects: [],
						setup_projects: [],
						setup_runs: [],
						features: [],
						items: [],
						projects: [],
						own_items: [],
						cards: [],
						decisions: [],
					}),
				refresh: () => Promise.resolve(),
			}}
			running={{
				loadRunning: () => new Promise(() => {}),
				stopRun: () => Promise.resolve(),
				runNow: () => Promise.resolve(),
				openEvents: () => ({
					addEventListener: () => {},
					close: () => {},
					readyState: 0,
				}),
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
	test("the four tabs render, in order", () => {
		openAt("/devflow");

		expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual([
			"Waiting for you",
			"Running now",
			"Projects",
			"Usage and history",
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
				name: "Personal-platform",
			}),
		).toBeTruthy();
		expect(panel.getByRole("article", { name: "Workers" })).toBeTruthy();
		expect(panel.queryByText("No project connected yet")).toBeNull();
	});

	test("a project's settings address opens its settings page under Projects, and its way back goes to the project", async () => {
		const nav = document.body.appendChild(
			document.createElement("platform-nav"),
		);
		openAt("/devflow/projects/0190a8f0-0000-7000-8000-000000000001/settings");

		expect(selectedTab()?.textContent).toBe("Projects");
		const panel = within(screen.getByRole("tabpanel"));
		expect(
			await panel.findByRole("heading", { name: "Settings" }),
		).toBeTruthy();
		// The crumb is the Projects tab's.
		expect(nav.getAttribute("crumb")).toBe("Dev flow / Projects");
		nav.remove();

		fireEvent.click(panel.getByRole("link", { name: "Personal-platform" }));
		expect(window.location.pathname).toBe(
			"/devflow/projects/0190a8f0-0000-7000-8000-000000000001",
		);
		expect(
			await within(screen.getByRole("tabpanel")).findByRole("article", {
				name: "Workers",
			}),
		).toBeTruthy();
	});

	test("the way back from a project's page goes to Projects", async () => {
		openAt("/devflow/projects/0190a8f0-0000-7000-8000-000000000001");

		fireEvent.click(
			await within(screen.getByRole("tabpanel")).findByRole("link", {
				name: "Projects",
			}),
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

describe("everything paused", () => {
	test("everything paused shows a strip on every tab with Continue everything", async () => {
		const STRIP = "Everything is paused. Nothing new starts in any project.";
		let paused = true;
		const asked: boolean[] = [];
		const pauses: PausesApi = {
			loadPaused: () =>
				Promise.resolve({
					paused,
					paused_at: paused ? "2026-11-17T12:00:00+00:00" : null,
				}),
			pauseAll: (value) => {
				asked.push(value);
				paused = value;
				return Promise.resolve();
			},
		};

		// On every tab, under the page's heading.
		for (const path of [
			"/devflow",
			"/devflow/running",
			"/devflow/projects",
			"/devflow/usage",
			`/devflow/projects/${PROJECT.id}`,
		]) {
			openAt(path, pauses);
			const strip = await screen.findByText(STRIP);
			expect(strip.closest("header")).toBe(
				screen
					.getByRole("heading", { level: 1, name: "Dev flow" })
					.closest("header"),
			);
			cleanup();
		}

		// Continue everything continues, and the strip goes.
		openAt("/devflow", pauses);
		await screen.findByText(STRIP);
		fireEvent.click(
			screen.getByRole("button", { name: "Continue everything" }),
		);
		await waitFor(() => expect(screen.queryByText(STRIP)).toBe(null));
		expect(asked).toEqual([false]);
	});

	test("nothing paused shows no strip", async () => {
		openAt("/devflow");
		await screen.findByRole("tab", { name: "Waiting for you" });
		await waitFor(() =>
			expect(screen.queryByText(/Everything is paused/)).toBe(null),
		);
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
