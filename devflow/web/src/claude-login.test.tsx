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
import { ApiError } from "./api/client";
import {
	ClaudeLogin,
	type ClaudeLoginStatus,
	NOT_A_TOKEN,
	NOT_REMOVED,
	NOT_SAVED,
} from "./claude-login";

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close. These stand in for the browser's, as projects-link.test.tsx's do.
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

const PURPOSE =
	"Agents run on your subscription with this. Make it with claude setup-token.";
const TOKEN = "sk-ant-oat01-dml0ZXN0LW9ubHktbm90LWEtcmVhbC10b2tlbg_AAAAAA";
const GIVEN = "2026-11-17T08:00:00+00:00";
const RENEW_BY = "2027-11-17T08:00:00+00:00";

const NONE: ClaudeLoginStatus = {
	set: false,
	given_at: null,
	renew_by: null,
	state: "none",
};

const at = (state: ClaudeLoginStatus["state"]): ClaudeLoginStatus => ({
	set: true,
	given_at: GIVEN,
	renew_by: RENEW_BY,
	state,
});

/** A day as the section writes one, in the test's own zone. */
const day = (time: string) =>
	new Intl.DateTimeFormat("en-GB", { dateStyle: "medium" }).format(
		new Date(time),
	);

function renderLogin(
	status: ClaudeLoginStatus,
	api: {
		saveLogin?: (token: string) => Promise<ClaudeLoginStatus>;
		removeLogin?: () => Promise<void>;
	} = {},
) {
	const calls = {
		loadLogin: vi.fn(() => Promise.resolve(status)),
		saveLogin: vi.fn(api.saveLogin ?? (() => Promise.resolve(at("ok")))),
		removeLogin: vi.fn(api.removeLogin ?? (() => Promise.resolve())),
	};
	const { container } = render(<ClaudeLogin {...calls} />);
	return { ...calls, container };
}

const section = () => screen.findByRole("article", { name: "Claude login" });

describe("the Claude login on the Projects tab", () => {
	test("nothing shows before the status answers", () => {
		render(
			<ClaudeLogin
				loadLogin={() => new Promise<never>(() => {})}
				saveLogin={vi.fn()}
				removeLogin={vi.fn()}
			/>,
		);

		expect(screen.queryByRole("article")).toBeNull();
	});

	test("not set, it says what it is for and offers a password field", async () => {
		renderLogin(NONE);
		const login = await section();

		expect(login.textContent).toContain(PURPOSE);
		expect(within(login).getByText("Not set yet.")).toBeTruthy();
		const field = within(login).getByLabelText("Claude token");
		expect(field.getAttribute("type")).toBe("password");
		expect(field.getAttribute("autocomplete")).toBe("off");
		// Nothing to save until something is typed, and nothing to remove.
		const save = within(login).getByRole("button", { name: "Save" });
		expect((save as HTMLButtonElement).disabled).toBe(true);
		expect(within(login).queryByRole("button", { name: "Remove" })).toBeNull();
		// Not the page's primary, which the GitHub link or "Connect a
		// repository" holds.
		expect(login.textContent).not.toContain("Needs you");
	});

	test("saving sends the token, empties the field and never shows it", async () => {
		const { saveLogin, container } = renderLogin(NONE);
		const login = await section();
		const field = within(login).getByLabelText("Claude token");

		fireEvent.change(field, { target: { value: TOKEN } });
		await act(async () => {
			fireEvent.click(within(login).getByRole("button", { name: "Save" }));
		});

		expect(saveLogin).toHaveBeenCalledWith(TOKEN);
		expect((field as HTMLInputElement).value).toBe("");
		expect(login.textContent).toContain(
			`Set on ${day(GIVEN)}, renew by ${day(RENEW_BY)}.`,
		);
		expect(within(login).getByRole("button", { name: "Replace" })).toBeTruthy();
		expect(within(login).getByRole("button", { name: "Remove" })).toBeTruthy();
		expect(container.innerHTML).not.toContain(TOKEN.slice(10));
	});

	test("a token that is not a setup token is refused on the field", async () => {
		const { container } = renderLogin(NONE, {
			saveLogin: () =>
				Promise.reject(
					new ApiError({
						status: 422,
						title: "That is not a setup token",
						detail: null,
					}),
				),
		});
		const login = await section();
		const field = within(login).getByLabelText("Claude token");

		fireEvent.change(field, { target: { value: "sk-ant-api03-a-key" } });
		await act(async () => {
			fireEvent.click(within(login).getByRole("button", { name: "Save" }));
		});

		expect(login.textContent).toContain(NOT_A_TOKEN);
		expect(field.getAttribute("aria-invalid")).toBe("true");
		expect((field as HTMLInputElement).value).toBe("");
		expect(within(login).getByText("Not set yet.")).toBeTruthy();
		expect(container.innerHTML).not.toContain("sk-ant-api03-a-key");
	});

	test("a save that fails otherwise says so as an alert", async () => {
		renderLogin(NONE, {
			saveLogin: () =>
				Promise.reject(
					new ApiError({ status: 0, title: "unreachable", detail: null }),
				),
		});
		const login = await section();

		fireEvent.change(within(login).getByLabelText("Claude token"), {
			target: { value: TOKEN },
		});
		await act(async () => {
			fireEvent.click(within(login).getByRole("button", { name: "Save" }));
		});

		expect(within(login).getByRole("alert").textContent).toBe(NOT_SAVED);
	});

	test("set, it says when it was given and when it is due", async () => {
		renderLogin(at("ok"));
		const login = await section();

		expect(login.textContent).toContain(
			`Set on ${day(GIVEN)}, renew by ${day(RENEW_BY)}.`,
		);
		expect(within(login).getByRole("button", { name: "Replace" })).toBeTruthy();
		expect(login.textContent).not.toContain("Needs you");
	});

	test("a month before it is due, it warns in the attention style", async () => {
		renderLogin(at("due_soon"));
		const login = await section();

		expect(login.textContent).toContain("Needs you");
		expect(login.textContent).toContain(
			`Set on ${day(GIVEN)}. Renew it by ${day(RENEW_BY)}: agents cannot run once it runs out.`,
		);
	});

	test("expired, it says it ran out", async () => {
		renderLogin(at("expired"));
		const login = await section();

		expect(login.textContent).toContain("Needs you");
		expect(login.textContent).toContain(
			`It ran out on ${day(RENEW_BY)}: agents cannot run until you replace it.`,
		);
	});

	test("removing it asks first, and then it is not set", async () => {
		const { removeLogin } = renderLogin(at("ok"));
		const login = await section();

		fireEvent.click(within(login).getByRole("button", { name: "Remove" }));
		expect(removeLogin).not.toHaveBeenCalled();
		const dialog = screen.getByRole("dialog", {
			name: "Remove the Claude login?",
		});
		await act(async () => {
			fireEvent.click(
				within(dialog).getByRole("button", { name: "Remove the login" }),
			);
		});

		expect(removeLogin).toHaveBeenCalledTimes(1);
		await waitFor(() =>
			expect(within(login).getByText("Not set yet.")).toBeTruthy(),
		);
		expect(within(login).getByRole("button", { name: "Save" })).toBeTruthy();
	});

	test("cancelling the confirmation removes nothing", async () => {
		const { removeLogin } = renderLogin(at("ok"));
		const login = await section();

		fireEvent.click(within(login).getByRole("button", { name: "Remove" }));
		fireEvent.click(
			within(screen.getByRole("dialog")).getByRole("button", {
				name: "Cancel",
			}),
		);

		expect(removeLogin).not.toHaveBeenCalled();
		expect(within(login).getByRole("button", { name: "Remove" })).toBeTruthy();
	});

	test("a removal that fails says so and keeps the login", async () => {
		renderLogin(at("ok"), { removeLogin: () => Promise.reject(new Error()) });
		const login = await section();

		fireEvent.click(within(login).getByRole("button", { name: "Remove" }));
		await act(async () => {
			fireEvent.click(
				within(screen.getByRole("dialog")).getByRole("button", {
					name: "Remove the login",
				}),
			);
		});

		expect(within(login).getByRole("alert").textContent).toBe(NOT_REMOVED);
		expect(within(login).getByRole("button", { name: "Replace" })).toBeTruthy();
	});
});
