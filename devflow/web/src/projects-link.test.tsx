import {
	act,
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import { App } from "./app";
import { GitHubLink, type GitHubLinkProps } from "./projects-link";
import type { Status } from "./status";

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close. These stand in for the browser's, as design/ui's dialog tests
// do; the top layer, the backdrop and Escape are the browser's own, and
// tested in e2e/tests/test_gallery.py.
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
	this.open = true;
	this.querySelector("button")?.focus();
};
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
	if (!this.open) return;
	this.open = false;
	this.dispatchEvent(new Event("close"));
};

const FAILED = "GitHub was not linked. Try again.";

afterEach(cleanup);

/** The page's projects, none, so a test of the link needs no service. */
const NO_PROJECTS = {
	loadProjects: () => Promise.resolve([]),
	loadRepositories: () => new Promise<never>(() => {}),
	connect: () => Promise.resolve(),
	disconnect: () => Promise.resolve(),
};

function renderLink(props: Partial<GitHubLinkProps>) {
	const actions = {
		start: vi.fn(() =>
			Promise.resolve("https://platform.test/devflow/fake-github/authorize"),
		),
		unlink: vi.fn(() => Promise.resolve()),
		go: vi.fn(),
	};
	render(
		<GitHubLink
			github={{ state: "none", login: null }}
			failed={false}
			{...actions}
			{...props}
		/>,
	);
	return actions;
}

describe("GitHub is one line with its state and its action", () => {
	test("linked, it shows the login and Unlink, behind a confirmation", async () => {
		const { unlink } = renderLink({
			github: { state: "linked", login: "octo-owner" },
		});

		const item = screen.getByRole("article", { name: "GitHub" });
		expect(within(item).getByText("linked as @octo-owner")).toBeTruthy();
		expect(within(item).getByText("Linked")).toBeTruthy();
		expect(within(item).queryByText("Needs you")).toBeNull();
		expect(
			within(item).queryByRole("button", { name: "Link GitHub" }),
		).toBeNull();
		const action = within(item).getByRole("button", { name: "Unlink" });
		// One action on the line, so Connect a repository stays the one primary.
		expect(within(item).getAllByRole("button")).toHaveLength(1);
		expect(action.className).toMatch(/ghost/);

		fireEvent.click(action);
		expect(unlink).not.toHaveBeenCalled();
		const dialog = screen.getByRole("dialog", { name: "Unlink GitHub?" });
		await act(async () => {
			fireEvent.click(
				within(dialog).getByRole("button", { name: "Unlink GitHub" }),
			);
		});

		expect(unlink).toHaveBeenCalledTimes(1);
	});

	test("cancelling the confirmation unlinks nothing", () => {
		const { unlink } = renderLink({
			github: { state: "linked", login: "octo-owner" },
		});

		fireEvent.click(screen.getByRole("button", { name: "Unlink" }));
		fireEvent.click(
			within(screen.getByRole("dialog")).getByRole("button", {
				name: "Cancel",
			}),
		);

		expect(unlink).not.toHaveBeenCalled();
	});

	test("not linked, it says so and Link GitHub, a secondary button, leads to the code host", async () => {
		const { start, go } = renderLink({
			github: { state: "none", login: null },
		});

		const item = screen.getByRole("article", { name: "GitHub" });
		expect(within(item).getByText("Not linked")).toBeTruthy();
		expect(within(item).queryByRole("alert")).toBeNull();
		const action = within(item).getByRole("button", { name: "Link GitHub" });
		expect(action.className).toMatch(/secondary/);
		expect(action.className).not.toMatch(/primary/);
		fireEvent.click(action);

		await waitFor(() =>
			expect(go).toHaveBeenCalledWith(
				"https://platform.test/devflow/fake-github/authorize",
			),
		);
		expect(start).toHaveBeenCalledTimes(1);
	});

	test("needs relink, it says the link expired, needs you, and Link again is a secondary button", async () => {
		const { go } = renderLink({
			github: { state: "needs_relink", login: "octo-owner" },
		});

		const item = screen.getByRole("article", { name: "GitHub" });
		expect(within(item).getByText("Expired")).toBeTruthy();
		expect(within(item).getByText("your link has expired")).toBeTruthy();
		// The item's mark for assistive technology, so it is not colour alone.
		expect(within(item).getByText("Needs you")).toBeTruthy();
		const action = within(item).getByRole("button", { name: "Link again" });
		expect(action.className).toMatch(/secondary/);
		fireEvent.click(action);

		await waitFor(() => expect(go).toHaveBeenCalled());
	});

	test("after a return that did not link, its error is a line under the item", () => {
		renderLink({ github: { state: "none", login: null }, failed: true });

		const item = screen.getByRole("article", { name: "GitHub" });
		const error = within(item).getByRole("alert");
		expect(error.textContent).toBe(FAILED);
		// Under the line, after the action.
		const action = within(item).getByRole("button", { name: "Link GitHub" });
		expect(action.compareDocumentPosition(error)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});

	test("a start that fails says so, and goes nowhere", async () => {
		const { go } = renderLink({
			start: vi.fn(() => Promise.reject(new Error("unreachable"))),
		});

		fireEvent.click(screen.getByRole("button", { name: "Link GitHub" }));

		await waitFor(() =>
			expect(screen.getByRole("alert").textContent).toBe(FAILED),
		);
		expect(go).not.toHaveBeenCalled();
	});

	test("before the status answers, it shows nothing", () => {
		renderLink({ github: undefined });

		expect(screen.queryByRole("article")).toBeNull();
		expect(screen.queryByRole("button")).toBeNull();
	});
});

describe("on the page", () => {
	const status = (github: Status["github"]): Status => ({
		code_host: "fake",
		github,
	});

	test("the Projects tab shows the link in Connections above its list, and Waiting for you does not", async () => {
		window.history.pushState(null, "", "/devflow/projects");
		const answer = Promise.resolve(status({ state: "none", login: null }));
		render(<App loadStatus={() => answer} projects={NO_PROJECTS} />);
		await act(async () => {
			await answer;
		});

		const panel = within(screen.getByRole("tabpanel"));
		const connections = panel.getByRole("region", { name: "Connections" });
		expect(
			within(connections).getByRole("article", { name: "GitHub" }),
		).toBeTruthy();
		expect(within(connections).getByText("Not linked")).toBeTruthy();
		expect(await panel.findByText("No project connected yet")).toBeTruthy();

		fireEvent.click(screen.getByRole("tab", { name: "Waiting for you" }));
		expect(screen.queryByRole("article", { name: "GitHub" })).toBeNull();
	});

	test("the address's link=failed is the message", async () => {
		window.history.pushState(null, "", "/devflow/projects?link=failed");
		const answer = Promise.resolve(status({ state: "none", login: null }));
		render(<App loadStatus={() => answer} projects={NO_PROJECTS} />);
		await act(async () => {
			await answer;
		});

		expect(screen.getByRole("alert").textContent).toBe(FAILED);
	});
});
