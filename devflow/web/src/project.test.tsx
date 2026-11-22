import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import {
	heldNotice,
	NO_WORKER,
	NOT_COPIED,
	NOT_COPIED_COMMAND,
	NOT_MADE,
	NOT_READ,
	PASTE,
	ProjectPage,
	type ProjectPageProps,
	SHOWN_ONCE,
	type Worker,
	type WorkerList,
} from "./project";
import type { Project } from "./projects";

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close, as in projects.test.tsx.
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
	this.open = true;
	this.querySelector("button")?.focus();
};
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
	if (!this.open) return;
	this.open = false;
	this.dispatchEvent(new Event("close"));
};

afterEach(cleanup);

const ID = "0190a8f0-0000-7000-8000-000000000001";
const NOW = Date.parse("2026-11-17T12:00:00Z");
const KEY = `dfw_${"a".repeat(43)}`;
const SAVE = `(umask 077 && mkdir -p ~/.devflow && printf 'DEVFLOW_PROJECT_KEY=%s\\n' '${KEY}' > ~/.devflow/personal-platform.env)`;
const START =
	"sed -n 's/^DEVFLOW_PROJECT_KEY=/Authorization: Bearer /p' ~/.devflow/personal-platform.env | curl -fsS -H @- … && \\\n  docker run -d --name devflow-personal-platform-1 devflow-personal-platform";

const PLATFORM: Project = {
	id: ID,
	installation_id: 41,
	repository_id: 9001,
	owner: "Adamniels",
	name: "Personal-platform",
	created_at: "2026-11-16T10:00:00+00:00",
};

const worker = (name: string, lastSeen: string): Worker => ({
	id: `0190a8f0-0000-7000-8000-0000000000${name.length}${name.length}`,
	name,
	recipe_version: "3",
	base_image: "platform-hosted/devflow-worker:2026.11.17",
	first_seen_at: "2026-11-17T09:00:00+00:00",
	last_seen_at: lastSeen,
	rebuild: null,
});

const EMPTY: WorkerList = {
	key_made_at: null,
	recipe_version: null,
	start_command: START,
	workers: [],
	held: null,
};

function renderPage(
	props: Partial<ProjectPageProps> & { list?: Partial<WorkerList> } = {},
) {
	let list: WorkerList = { ...EMPTY, ...props.list };
	const api = {
		loadProjects: vi.fn(() => Promise.resolve([PLATFORM])),
		loadWorkers: vi.fn(() => Promise.resolve(list)),
		makeKey: vi.fn(() => {
			list = { ...list, key_made_at: "2026-11-17T12:00:00+00:00" };
			return Promise.resolve({ key: KEY, save_command: SAVE });
		}),
		loadSettings: vi.fn(() =>
			Promise.resolve({
				pickup_label: "devflow",
				hold_label: "devflow-hold",
				attention_label: "devflow-attention",
				review_label: "devflow-review",
				waits_for_owner: "never",
				wait_when_marked: true,
				wait_when_paths: true,
				wait_when_decision: true,
				wait_paths: [],
				trusted_logins: [{ login: "octo-owner", mine: true }],
				model_reading: "opus",
				model_building: "opus",
				model_reviewing: "opus",
				run_minutes: 90,
				run_turns: 200,
				fix_rounds_card: 3,
				fix_rounds_feature: 3,
				questions: true,
			}),
		),
		saveSettings: vi.fn(() =>
			Promise.reject(new Error("the project page's tests do not save")),
		),
		loadCards: vi.fn(() => Promise.resolve({ cards: [], features: [] })),
		copy: vi.fn(() => Promise.resolve()),
		back: vi.fn(),
	};
	const { list: _, ...rest } = props;
	render(<ProjectPage id={ID} now={() => NOW} {...api} {...rest} />);
	return api;
}

const panel = () => screen.findByRole("article", { name: "Workers" });

describe("a project's page", () => {
	test("is headed by the repository's name, with the way back", async () => {
		const { back } = renderPage();

		expect(
			(
				await screen.findByRole("heading", {
					level: 2,
					name: "Adamniels/Personal-platform",
				})
			).textContent,
		).toBe("Adamniels/Personal-platform");
		const link = screen.getByRole("link", { name: "Back to Projects" });
		expect(link.getAttribute("href")).toBe("/devflow/projects");
		fireEvent.click(link);
		expect(back).toHaveBeenCalledOnce();
	});

	test("a project the caller does not have is not found", async () => {
		renderPage({ id: "0190a8f0-0000-7000-8000-00000000ffff" });

		expect(await screen.findByText("There is no such project")).toBeTruthy();
		expect(screen.queryByRole("article", { name: "Workers" })).toBeNull();
		expect(screen.queryByRole("article", { name: "Settings" })).toBeNull();
	});

	test("shows the project's settings, then its cards, then its workers", async () => {
		const { loadSettings, loadCards } = renderPage();

		const settings = await screen.findByRole("article", { name: "Settings" });
		const cards = await screen.findByRole("article", { name: "Cards" });
		const workers = await panel();
		expect(loadSettings).toHaveBeenCalledWith(ID);
		expect(loadCards).toHaveBeenCalledWith(ID);
		expect(
			settings.compareDocumentPosition(cards) &
				Node.DOCUMENT_POSITION_FOLLOWING,
		).toBeTruthy();
		expect(
			cards.compareDocumentPosition(workers) & Node.DOCUMENT_POSITION_FOLLOWING,
		).toBeTruthy();
	});

	test("without a key or a worker, it offers to make the key", async () => {
		renderPage();

		const workers = within(await panel());
		expect(await workers.findByText(NO_WORKER)).toBeTruthy();
		expect(workers.getByText("It has no key yet.")).toBeTruthy();
		expect(workers.getByRole("button", { name: "Make a key" })).toBeTruthy();
		expect(
			workers.queryByRole("button", { name: "Make a new key" }),
		).toBeNull();
	});
});

describe("the worker key", () => {
	test("is shown once, with a button that copies it", async () => {
		const { makeKey, copy, loadWorkers } = renderPage();
		const workers = within(await panel());

		fireEvent.click(await workers.findByRole("button", { name: "Make a key" }));

		const shown = within(
			await workers.findByRole("region", { name: "The new key" }),
		);
		expect(shown.getByText(KEY).tagName).toBe("CODE");
		expect(shown.getByText(SHOWN_ONCE)).toBeTruthy();
		expect(makeKey).toHaveBeenCalledWith(ID);
		// Read again, so the page knows there is a key now.
		await waitFor(() => expect(loadWorkers).toHaveBeenCalledTimes(2));
		expect(
			await workers.findByRole("button", { name: "Make a new key" }),
		).toBeTruthy();

		fireEvent.click(shown.getByRole("button", { name: "Copy the key" }));

		expect(await shown.findByRole("button", { name: "Copied" })).toBeTruthy();
		expect(copy).toHaveBeenCalledWith(KEY);
	});

	test("a key that could not be copied says so", async () => {
		renderPage({ copy: () => Promise.reject(new Error("denied")) });
		const workers = within(await panel());
		fireEvent.click(await workers.findByRole("button", { name: "Make a key" }));
		const shown = within(
			await workers.findByRole("region", { name: "The new key" }),
		);

		fireEvent.click(shown.getByRole("button", { name: "Copy the key" }));

		expect((await shown.findByRole("alert")).textContent).toBe(NOT_COPIED);
		expect(shown.getByText(KEY)).toBeTruthy();
	});

	test("once one was made, the page shows when and never the key", async () => {
		renderPage({
			list: { key_made_at: "2026-11-17T08:30:00+00:00", workers: [] },
		});
		const workers = within(await panel());

		expect(await workers.findByText(/^Its key was made/)).toBeTruthy();
		expect(
			workers
				.getByText(/^Its key was made/)
				.querySelector("time")
				?.getAttribute("dateTime"),
		).toBe("2026-11-17T08:30:00+00:00");
		expect(workers.queryByRole("region", { name: "The new key" })).toBeNull();
		expect(workers.queryByRole("button", { name: "Make a key" })).toBeNull();
	});

	test("making a new one warns that the old one stops working, and waits for a yes", async () => {
		const { makeKey } = renderPage({
			list: { key_made_at: "2026-11-17T08:30:00+00:00", workers: [] },
		});
		const workers = within(await panel());

		fireEvent.click(
			await workers.findByRole("button", { name: "Make a new key" }),
		);

		const dialog = within(
			screen.getByRole("dialog", { name: "Make a new key?" }),
		);
		expect(dialog.getByText(/The old key stops working at once/)).toBeTruthy();
		expect(makeKey).not.toHaveBeenCalled();
		fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));
		expect(makeKey).not.toHaveBeenCalled();

		fireEvent.click(workers.getByRole("button", { name: "Make a new key" }));
		fireEvent.click(
			within(screen.getByRole("dialog", { name: "Make a new key?" })).getByRole(
				"button",
				{
					name: "Make a new key",
				},
			),
		);

		expect(await workers.findByText(KEY)).toBeTruthy();
		expect(makeKey).toHaveBeenCalledOnce();
	});

	test("a key that was not made says so", async () => {
		renderPage({ makeKey: () => Promise.reject(new Error("503")) });
		const workers = within(await panel());

		fireEvent.click(await workers.findByRole("button", { name: "Make a key" }));

		expect((await workers.findByRole("alert")).textContent).toBe(NOT_MADE);
		expect(workers.queryByRole("region", { name: "The new key" })).toBeNull();
	});
});

describe("starting a worker", () => {
	test("the start command is shown before a key, with a button that copies it", async () => {
		const { copy } = renderPage();
		const start = within(
			await screen.findByRole("region", { name: "Start a worker" }),
		);

		expect(start.getByText(PASTE)).toBeTruthy();
		expect(start.queryByRole("region", { name: "Save the key" })).toBeNull();
		const command = within(
			start.getByRole("region", { name: "Build and start the worker" }),
		);
		expect(command.getByText(START, { normalizer: (t) => t }).tagName).toBe(
			"CODE",
		);
		expect(
			command.getByText(START, { normalizer: (t) => t }).textContent,
		).not.toContain("dfw_");

		fireEvent.click(
			command.getByRole("button", { name: "Copy the start command" }),
		);

		expect(await command.findByRole("button", { name: "Copied" })).toBeTruthy();
		expect(copy).toHaveBeenCalledWith(START);
	});

	test("the save command is shown once, after the key is made", async () => {
		const { copy } = renderPage();
		const workers = within(await panel());
		fireEvent.click(await workers.findByRole("button", { name: "Make a key" }));

		const save = within(
			await workers.findByRole("region", { name: "Save the key" }),
		);
		expect(save.getByText(SAVE, { normalizer: (t) => t })).toBeTruthy();
		fireEvent.click(
			save.getByRole("button", { name: "Copy the save command" }),
		);
		expect(await save.findByRole("button", { name: "Copied" })).toBeTruthy();
		expect(copy).toHaveBeenCalledWith(SAVE);
		// The start command stays, after it.
		expect(
			workers.getByRole("region", { name: "Build and start the worker" }),
		).toBeTruthy();
	});

	test("a command that could not be copied says so", async () => {
		renderPage({ copy: () => Promise.reject(new Error("denied")) });
		const command = within(
			await screen.findByRole("region", { name: "Build and start the worker" }),
		);

		fireEvent.click(
			command.getByRole("button", { name: "Copy the start command" }),
		);

		expect((await command.findByRole("alert")).textContent).toBe(
			NOT_COPIED_COMMAND,
		);
	});
});

describe("a project that hands out no work", () => {
	test("says it was stopped by what landed, and where to resume it", async () => {
		renderPage({ list: { held: "stopped" } });

		const workers = await panel();
		await within(workers).findByText(heldNotice("stopped"));
		expect(heldNotice("stopped")).toContain("Waiting for you");
	});

	test("says when its runs keep failing outside their cards", async () => {
		const said =
			"Dev flow cannot run Fodr's cards: Claude Code is not logged in with the token";
		renderPage({ list: { cannot_run: said } });

		const workers = await panel();
		await within(workers).findByText(said);
		expect(within(workers).getByText("Cannot run")).toBeTruthy();
	});

	test("says when its default branch could not be read", async () => {
		renderPage({ list: { held: "unwatched" } });

		await within(await panel()).findByText(heldNotice("unwatched"));
	});

	test("says nothing while it hands out work", async () => {
		renderPage();

		const workers = await panel();
		await within(workers).findByText("It has no key yet.");
		expect(within(workers).queryByText(/hands out no work/)).toBeNull();
	});
});

describe("the workers", () => {
	test("a project that hands out no work for the Claude login says why", async () => {
		renderPage({ list: { held: "no_claude_login" } });
		const workers = within(await panel());
		expect(
			await workers.findByText(heldNotice("no_claude_login")),
		).toBeTruthy();
		expect(workers.getByText("No work")).toBeTruthy();
		expect(heldNotice("no_claude_login")).toContain("Projects tab");
	});

	test("a login past its renewal says to give a new one", async () => {
		renderPage({ list: { held: "claude_login_expired" } });
		const workers = within(await panel());
		expect(
			await workers.findByText(heldNotice("claude_login_expired")),
		).toBeTruthy();
		expect(heldNotice("claude_login_expired")).toContain("past its renewal");
	});

	test("a project that hands out work says nothing of it", async () => {
		renderPage();
		const workers = within(await panel());
		await workers.findByText(PASTE);
		expect(workers.queryByText("No work")).toBeNull();
	});

	test("one on an old recipe or base image says to run the command again", async () => {
		renderPage({
			list: {
				key_made_at: "2026-11-17T08:30:00+00:00",
				workers: [
					{
						...worker("old-recipe", "2026-11-17T11:59:00+00:00"),
						rebuild: "recipe",
					},
					{
						...worker("old-base", "2026-11-17T11:59:00+00:00"),
						rebuild: "base_image",
					},
					worker("current", "2026-11-17T11:59:00+00:00"),
				],
			},
		});

		const [recipe, base, current] = within(
			await screen.findByRole("list", { name: "Workers" }),
		).getAllByRole("listitem");
		expect(
			within(recipe as HTMLElement).getByText(
				"Personal-platform's recipe has changed: run the command again",
			),
		).toBeTruthy();
		expect(within(recipe as HTMLElement).getByText("Rebuild")).toBeTruthy();
		expect(
			within(base as HTMLElement).getByText(
				"A new base image was released: run the command again",
			),
		).toBeTruthy();
		expect(within(current as HTMLElement).queryByText("Rebuild")).toBeNull();
		expect(
			within(current as HTMLElement).queryByText(/run the command again/),
		).toBeNull();
	});

	test("each is listed with what it runs and when it was last seen", async () => {
		renderPage({
			list: {
				key_made_at: "2026-11-17T08:30:00+00:00",
				workers: [worker("fodr-worker-1", "2026-11-17T11:57:00+00:00")],
			},
		});

		const list = within(await screen.findByRole("list", { name: "Workers" }));
		const row = within(list.getByRole("listitem"));
		expect(row.getByText("fodr-worker-1")).toBeTruthy();
		expect(row.getByText("3")).toBeTruthy();
		expect(
			row.getByText("platform-hosted/devflow-worker:2026.11.17"),
		).toBeTruthy();
		expect(row.getByText(/^Last seen/).textContent).toBe(
			"Last seen 3 minutes ago",
		);
		expect(screen.queryByText(NO_WORKER)).toBeNull();
	});

	test("one quiet for ten minutes shows as not seen since then", async () => {
		renderPage({
			list: {
				key_made_at: "2026-11-17T08:30:00+00:00",
				workers: [
					worker("quiet", "2026-11-17T11:50:00+00:00"),
					worker("recent", "2026-11-17T11:50:01+00:00"),
				],
			},
		});

		const [quiet, recent] = within(
			await screen.findByRole("list", { name: "Workers" }),
		).getAllByRole("listitem");
		const since = within(quiet as HTMLElement).getByText(/^Not seen since/);
		expect(since.querySelector("time")?.getAttribute("dateTime")).toBe(
			"2026-11-17T11:50:00+00:00",
		);
		expect(within(quiet as HTMLElement).queryByText(/^Last seen/)).toBeNull();
		expect(
			within(recent as HTMLElement).getByText(/^Last seen/).textContent,
		).toBe("Last seen 9 minutes ago");
	});

	test("a list that could not be read says so", async () => {
		renderPage({ loadWorkers: () => Promise.reject(new Error("503")) });
		const workers = within(await panel());

		expect((await workers.findByRole("alert")).textContent).toBe(NOT_READ);
	});

	test("a worker's name and what it reports are shown as text", async () => {
		const markup = '<img src=x onerror="window.pwned=1">';
		renderPage({
			list: {
				key_made_at: "2026-11-17T08:30:00+00:00",
				workers: [
					{
						...worker("w", "2026-11-17T11:59:00+00:00"),
						name: markup,
						base_image: markup,
					},
				],
			},
		});

		const list = await screen.findByRole("list", { name: "Workers" });
		expect(within(list).getAllByText(markup)).toHaveLength(2);
		expect(list.querySelector("img")).toBeNull();
	});
});
