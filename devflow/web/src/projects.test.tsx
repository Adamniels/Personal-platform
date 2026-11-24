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
	RUNNER_LINE,
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
	last_read_at: "2026-11-17T11:58:00+00:00",
	summary: {
		cards: { needs_you: 0, in_progress: 0, queued: 0, done: 0 },
		workers: { count: 0, last_seen_at: null, held: null, cannot_run: null },
		setting_up: null,
		feature: null,
	},
};

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
	render(<Projects linked={true} {...actions} {...props} />);
	return actions;
}

describe("the projects on the Projects tab", () => {
	test("lists each project by its owner and name, with how it is read", async () => {
		renderProjects({ projects: [PLATFORM] });

		const list = await screen.findByRole("region", { name: "Projects" });
		expect(within(list).getByText("Adamniels/Personal-platform")).toBeTruthy();
		// The name opens the project's page, an address of its own.
		const open = within(list).getByRole("link", {
			name: "Adamniels/Personal-platform",
		});
		expect(open.getAttribute("href")).toBe(`/devflow/projects/${PLATFORM.id}`);
		fireEvent.click(open);
		expect(window.location.pathname).toBe(`/devflow/projects/${PLATFORM.id}`);
		expect(within(list).getByText(RUNNER_LINE)).toBeTruthy();
		expect(
			within(list).getByRole("button", {
				name: "Disconnect Adamniels/Personal-platform",
			}),
		).toBeTruthy();
		expect(RUNNER_LINE).toBe("Read the way this repository's runner writes.");
	});

	test("with none, it says so", async () => {
		renderProjects({ projects: [] });

		expect(await screen.findByText("No project connected yet")).toBeTruthy();
		expect(screen.queryByRole("region", { name: "Projects" })).toBeNull();
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
