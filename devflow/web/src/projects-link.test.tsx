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

const INVITE =
	"Link your GitHub account to connect repositories and to merge and comment as yourself.";
const FAILED = "GitHub was not linked. Try again.";
const EXPIRED = "Your GitHub link has expired.";

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

describe("the link on the Projects tab", () => {
	test("unlinked, it invites a link and leads to the code host", async () => {
		const { start, go } = renderLink({
			github: { state: "none", login: null },
		});

		expect(screen.getByText(INVITE)).toBeTruthy();
		expect(screen.queryByText(FAILED)).toBeNull();
		fireEvent.click(screen.getByRole("button", { name: "Link GitHub" }));

		await waitFor(() =>
			expect(go).toHaveBeenCalledWith(
				"https://platform.test/devflow/fake-github/authorize",
			),
		);
		expect(start).toHaveBeenCalledTimes(1);
	});

	test("linked, it names the login and unlinks behind a confirmation", async () => {
		const { unlink } = renderLink({
			github: { state: "linked", login: "octo-owner" },
		});

		expect(screen.getByText("Linked as @octo-owner")).toBeTruthy();
		expect(screen.queryByText(INVITE)).toBeNull();
		expect(screen.queryByRole("button", { name: "Link GitHub" })).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Unlink" }));
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

	test("expired, it asks for the link again in the attention style", async () => {
		const { go } = renderLink({
			github: { state: "needs_relink", login: "octo-owner" },
		});

		const card = screen.getByRole("article", { name: "GitHub" });
		expect(within(card).getByText(EXPIRED)).toBeTruthy();
		// The card's mark for assistive technology, so it is not colour alone.
		expect(within(card).getByText("Needs you")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Link again" }));

		await waitFor(() => expect(go).toHaveBeenCalled());
	});

	test("after a return that did not link, it says so beside the invitation", () => {
		renderLink({ github: { state: "none", login: null }, failed: true });

		expect(screen.getByRole("alert").textContent).toBe(FAILED);
		expect(screen.getByRole("button", { name: "Link GitHub" })).toBeTruthy();
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

	test("the Projects tab shows the link above its list, and Waiting for you does not", async () => {
		window.history.pushState(null, "", "/devflow/projects");
		const answer = Promise.resolve(status({ state: "none", login: null }));
		render(<App loadStatus={() => answer} projects={NO_PROJECTS} />);
		await act(async () => {
			await answer;
		});

		const panel = within(screen.getByRole("tabpanel"));
		expect(panel.getByRole("article", { name: "GitHub" })).toBeTruthy();
		expect(panel.getByText(INVITE)).toBeTruthy();
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
