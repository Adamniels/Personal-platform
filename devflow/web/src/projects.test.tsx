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
	type Connectable,
	NOT_CONNECTED,
	NOT_DISCONNECTED,
	NOT_INSTALLED,
	NOT_READ,
	type Project,
	Projects,
	type ProjectsProps,
} from "./projects";

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close, as in projects-link.test.tsx.
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

const PLATFORM: Project = {
	id: "0190a8f0-0000-7000-8000-000000000001",
	installation_id: 41,
	repository_id: 9001,
	owner: "Adamniels",
	name: "Personal-platform",
	created_at: "2026-11-16T10:00:00+00:00",
	summary: {
		cards: { needs_you: 0, in_progress: 0, queued: 0, done: 0 },
		workers: { count: 0, last_seen_at: null, held: null, cannot_run: null },
		setting_up: null,
		feature: null,
	},
};

/** The test's clock: a minute after FODR's worker was last seen. */
const NOW = Date.parse("2026-11-24T12:00:00+00:00");

/** A project running: two of its nine cards need me, one builds, and its
 * worker was seen a minute ago. */
const FODR: Project = {
	...PLATFORM,
	id: "0190a8f0-0000-7000-8000-000000000002",
	repository_id: 9003,
	name: "fodr",
	summary: {
		cards: { needs_you: 2, in_progress: 1, queued: 3, done: 3 },
		workers: {
			count: 1,
			last_seen_at: "2026-11-24T11:59:00+00:00",
			held: null,
			cannot_run: null,
		},
		setting_up: null,
		feature: {
			number: 12,
			title: "Export the feeding plan",
			landed: 1,
			total: 3,
		},
	},
};

/** FODR's summary with its workers changed. */
const workers = (
	change: Partial<Project["summary"]["workers"]>,
): Project["summary"] => ({
	...FODR.summary,
	workers: { ...FODR.summary.workers, ...change },
});

const CONNECTABLE: Connectable = {
	installations: [
		{
			id: 41,
			account: "Adamniels",
			repositories: [
				{
					id: 9002,
					owner: "Adamniels",
					name: "habit-tracker",
					connected: false,
				},
				{
					id: 9001,
					owner: "Adamniels",
					name: "Personal-platform",
					connected: true,
				},
			],
		},
		{
			id: 52,
			account: "uppsala-lab",
			repositories: [
				{ id: 9101, owner: "uppsala-lab", name: "thesis", connected: false },
			],
		},
	],
	install_url: "https://github.com/apps/platform-devflow/installations/new",
};

function renderProjects(
	props: Partial<ProjectsProps> & { projects?: Project[] } = {},
) {
	let projects = props.projects ?? [];
	const actions = {
		loadProjects: vi.fn(() => Promise.resolve(projects)),
		loadRepositories: vi.fn(() => Promise.resolve(CONNECTABLE)),
		connect: vi.fn((_installation: number, repository: number) => {
			projects = [
				...projects,
				{ ...PLATFORM, id: `new-${repository}`, repository_id: repository },
			];
			return Promise.resolve();
		}),
		disconnect: vi.fn((id: string) => {
			projects = projects.filter((project) => project.id !== id);
			return Promise.resolve();
		}),
	};
	render(<Projects linked={true} now={() => NOW} {...actions} {...props} />);
	return actions;
}

describe("the projects on the Projects tab", () => {
	test("the heading says Projects, with Connect a repository as the view's one primary", async () => {
		renderProjects({ projects: [PLATFORM] });

		const list = await screen.findByRole("region", { name: "Projects" });
		const heading = within(list).getByRole("heading", { name: "Projects" });
		const connect = within(list).getByRole("button", {
			name: "Connect a repository",
		});
		expect(connect.className).toMatch(/primary/);
		expect(heading.compareDocumentPosition(connect)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		// The heading comes before the cards.
		const card = within(list).getByRole("article", {
			name: "Adamniels/Personal-platform",
		});
		expect(connect.compareDocumentPosition(card)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});

	test("each project is a card with its needs-you count, its ticks and its three rows", async () => {
		renderProjects({ projects: [FODR] });

		const card = await screen.findByRole("article", { name: "Adamniels/fodr" });
		// The hero label is the name, the card label the full name.
		expect(within(card).getByText("fodr")).toBeTruthy();
		// It needs me, so it takes the accent border, said in words too.
		expect(within(card).getByText("Needs you")).toBeTruthy();

		// The count in thin numerals, with its two line caption.
		const count = within(card).getByText("2");
		expect(count.className).toMatch(/count/);
		expect(within(card).getByText("need you")).toBeTruthy();
		expect(within(card).getByText("of 9 cards")).toBeTruthy();

		// One tick per card, the ones that need me first and in --accent.
		const ticks = within(card).getByRole("img", {
			name: "2 of 9 cards need you",
		});
		const each = Array.from(ticks.children);
		expect(each).toHaveLength(9);
		expect(each.map((tick) => /needsYou/.test(tick.className))).toEqual([
			true,
			true,
			false,
			false,
			false,
			false,
			false,
			false,
			false,
		]);

		// The three rows: what is building, the feature, the worker.
		const rows = within(card).getAllByRole("listitem");
		expect(rows).toHaveLength(3);
		expect(rows[0]?.textContent).toContain("Building");
		expect(rows[0]?.textContent).toContain("1 in progress");
		expect(rows[0]?.textContent).toContain("3 queued");
		expect(rows[1]?.textContent).toContain("Feature");
		expect(rows[1]?.textContent).toContain("Export the feeding plan");
		expect(rows[1]?.textContent).toContain("1 / 3 landed");
		expect(rows[2]?.textContent).toContain("Worker");
		expect(rows[2]?.textContent).toContain("1 worker · seen 1 minute ago");
		expect(within(rows[2] as HTMLElement).getByText("Running")).toBeTruthy();

		// No banner: it can run.
		expect(
			within(card).queryByText("No worker yet, so no card can run."),
		).toBeNull();
	});

	test("a card's arrow link opens its project's page, and Disconnect sits at its foot", async () => {
		renderProjects({ projects: [PLATFORM] });

		const card = await screen.findByRole("article", {
			name: "Adamniels/Personal-platform",
		});
		const open = within(card).getByRole("link", {
			name: "Open Adamniels/Personal-platform",
		});
		expect(open.getAttribute("href")).toBe(`/devflow/projects/${PLATFORM.id}`);
		fireEvent.click(open);
		expect(window.location.pathname).toBe(`/devflow/projects/${PLATFORM.id}`);
		const disconnect = within(card).getByRole("button", {
			name: "Disconnect Adamniels/Personal-platform",
		});
		expect(disconnect.className).toMatch(/ghost/);
	});

	test("a project that cannot run yet shows a Needs you banner and its one action", async () => {
		const setting: Project = {
			...FODR,
			id: "setting",
			name: "setting",
			summary: {
				...FODR.summary,
				cards: { needs_you: 0, in_progress: 0, queued: 0, done: 0 },
				workers: { count: 0, last_seen_at: null, held: null, cannot_run: null },
				setting_up: { done: 2, total: 6 },
				feature: null,
			},
		};
		const noWorker: Project = {
			...FODR,
			id: "no-worker",
			name: "no-worker",
			summary: {
				...FODR.summary,
				cards: { needs_you: 0, in_progress: 0, queued: 0, done: 0 },
				workers: { count: 0, last_seen_at: null, held: null, cannot_run: null },
				feature: null,
			},
		};
		const held: Project = {
			...FODR,
			id: "held",
			name: "held",
			summary: workers({ held: "no_claude_login" }),
		};
		const cannot: Project = {
			...FODR,
			id: "cannot",
			name: "cannot",
			summary: workers({
				cannot_run:
					"Dev flow cannot run Adamniels/cannot's cards: the network is down",
			}),
		};
		renderProjects({ projects: [setting, noWorker, held, cannot] });

		const expected: [Project, string, string][] = [
			[setting, "Setting up: 2 of 6 steps done.", "Continue setting up"],
			[noWorker, "No worker yet, so no card can run.", "Set up a worker"],
			[
				held,
				"No work is handed out until you give your Claude login on the Projects tab.",
				"Open the project",
			],
			[
				cannot,
				"Dev flow cannot run Adamniels/cannot's cards: the network is down",
				"Open the project",
			],
		];
		for (const [project, message, action] of expected) {
			const card = await screen.findByRole("article", {
				name: `Adamniels/${project.name}`,
			});
			expect(within(card).getByText(message)).toBeTruthy();
			// The banner is in place of the count and the ticks.
			expect(within(card).queryByRole("img")).toBeNull();
			expect(within(card).queryByText("need you")).toBeNull();
			// Its one action links the project's page.
			const link = within(card).getByRole("link", { name: action });
			expect(link.getAttribute("href")).toBe(`/devflow/projects/${project.id}`);
			expect(link.className).toMatch(/secondary/);
			// Needs you, said once by the banner, and the card's outline.
			expect(within(card).getAllByText("Needs you").length).toBeGreaterThan(0);
		}
	});

	test("with none, it says so under the heading", async () => {
		renderProjects({ projects: [] });

		const list = await screen.findByRole("region", { name: "Projects" });
		expect(
			await within(list).findByText("No project connected yet"),
		).toBeTruthy();
		expect(within(list).queryByRole("article")).toBeNull();
	});

	test("connecting is offered only when GitHub is linked", async () => {
		renderProjects({ linked: false });

		const button = await screen.findByRole("button", {
			name: "Connect a repository",
		});
		expect((button as HTMLButtonElement).disabled).toBe(true);
	});

	test("connecting lists the repositories by account, the connected ones marked, and connects one", async () => {
		const { connect, loadRepositories, loadProjects } = renderProjects({
			projects: [PLATFORM],
		});

		fireEvent.click(
			await screen.findByRole("button", { name: "Connect a repository" }),
		);

		const mine = await screen.findByRole("region", { name: "Adamniels" });
		const lab = screen.getByRole("region", { name: "uppsala-lab" });
		// Each group shows the account it is on, not only names it.
		expect(
			within(lab).getByRole("columnheader", { name: "uppsala-lab" }),
		).toBeTruthy();
		// And the empty list of projects gives way to the choice.
		expect(screen.queryByText("No project connected yet")).toBeNull();
		expect(loadRepositories).toHaveBeenCalledTimes(1);
		expect(within(lab).getByText("uppsala-lab/thesis")).toBeTruthy();
		// The connected one is marked and cannot be chosen.
		expect(within(mine).getByText("Connected")).toBeTruthy();
		expect(
			within(mine).queryByRole("button", {
				name: "Connect Adamniels/Personal-platform",
			}),
		).toBeNull();

		fireEvent.click(
			within(mine).getByRole("button", {
				name: "Connect Adamniels/habit-tracker",
			}),
		);

		await waitFor(() => expect(connect).toHaveBeenCalledWith(41, 9002));
		// The list is read again, and the choice closes.
		await waitFor(() => expect(loadProjects).toHaveBeenCalledTimes(2));
		await waitFor(() =>
			expect(screen.queryByRole("region", { name: "Adamniels" })).toBeNull(),
		);
	});

	test("with the app installed nowhere, it says so and links to installing it", async () => {
		renderProjects({
			loadRepositories: vi.fn(() =>
				Promise.resolve({
					installations: [],
					install_url: CONNECTABLE.install_url,
				}),
			),
		});

		fireEvent.click(
			await screen.findByRole("button", { name: "Connect a repository" }),
		);

		expect(await screen.findByText(NOT_INSTALLED)).toBeTruthy();
		expect(NOT_INSTALLED).toBe(
			"The GitHub App is not installed on any repository you can reach.",
		);
		const install = screen.getByRole("link", { name: "Install it on GitHub" });
		expect(install.getAttribute("href")).toBe(CONNECTABLE.install_url);
	});

	test("a failure to read or to connect says so", async () => {
		const unread = vi.fn(() => Promise.reject(new Error("down")));
		const { connect } = renderProjects({ loadRepositories: unread });
		fireEvent.click(
			await screen.findByRole("button", { name: "Connect a repository" }),
		);
		expect((await screen.findByRole("alert")).textContent).toBe(NOT_READ);
		expect(unread).toHaveBeenCalledTimes(1);
		expect(connect).not.toHaveBeenCalled();
		cleanup();

		const refused = vi.fn(() => Promise.reject(new Error("refused")));
		renderProjects({ connect: refused });
		fireEvent.click(
			await screen.findByRole("button", { name: "Connect a repository" }),
		);
		fireEvent.click(
			await screen.findByRole("button", { name: "Connect uppsala-lab/thesis" }),
		);
		expect((await screen.findByRole("alert")).textContent).toBe(NOT_CONNECTED);
		expect(refused).toHaveBeenCalledWith(52, 9101);
	});

	test("disconnecting asks first, saying what is removed", async () => {
		const { disconnect, loadProjects } = renderProjects({
			projects: [PLATFORM],
		});

		fireEvent.click(
			await screen.findByRole("button", {
				name: "Disconnect Adamniels/Personal-platform",
			}),
		);
		const dialog = screen.getByRole("dialog", {
			name: "Disconnect Adamniels/Personal-platform?",
		});
		expect(dialog.textContent).toContain(
			"Dev flow forgets this project and everything it read for it. Nothing changes on GitHub.",
		);

		// Cancelling keeps it.
		fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
		expect(disconnect).not.toHaveBeenCalled();

		fireEvent.click(
			screen.getByRole("button", {
				name: "Disconnect Adamniels/Personal-platform",
			}),
		);
		fireEvent.click(
			within(
				screen.getByRole("dialog", {
					name: "Disconnect Adamniels/Personal-platform?",
				}),
			).getByRole("button", { name: "Disconnect" }),
		);

		await waitFor(() => expect(disconnect).toHaveBeenCalledWith(PLATFORM.id));
		await waitFor(() => expect(loadProjects).toHaveBeenCalledTimes(2));
		expect(await screen.findByText("No project connected yet")).toBeTruthy();
	});

	test("a failed disconnect says so and keeps the project", async () => {
		renderProjects({
			projects: [PLATFORM],
			disconnect: vi.fn(() => Promise.reject(new Error("refused"))),
		});

		fireEvent.click(
			await screen.findByRole("button", {
				name: "Disconnect Adamniels/Personal-platform",
			}),
		);
		fireEvent.click(
			within(
				screen.getByRole("dialog", {
					name: "Disconnect Adamniels/Personal-platform?",
				}),
			).getByRole("button", { name: "Disconnect" }),
		);

		expect((await screen.findByRole("alert")).textContent).toBe(
			NOT_DISCONNECTED,
		);
		expect(screen.getByText("Adamniels/Personal-platform")).toBeTruthy();
	});
});
