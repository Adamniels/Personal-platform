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

/** Opens the token field with the line's one action. */
function open(login: HTMLElement, action: string) {
	fireEvent.click(within(login).getByRole("button", { name: action }));
	return within(login).getByLabelText("Claude token");
}

describe("the Claude login is one line, and giving a token opens its field", () => {
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

	test("not set, it says so, and its field is hidden until Give a token opens it below the line", async () => {
		renderLogin(NONE);
		const login = await section();

		expect(within(login).getByText("Not set")).toBeTruthy();
		expect(
			within(login).getByText("make one with claude setup-token"),
		).toBeTruthy();
		expect(within(login).queryByLabelText("Claude token")).toBeNull();
		expect(login.textContent).not.toContain("Needs you");
		const give = within(login).getByRole("button", { name: "Give a token" });
		expect(give.className).toMatch(/ghost/);

		const field = open(login, "Give a token");
		expect(field.getAttribute("type")).toBe("password");
		expect(field.getAttribute("autocomplete")).toBe("off");
		// Below the line, and what it is for said as its help.
		expect(
			give.compareDocumentPosition(field) & Node.DOCUMENT_POSITION_FOLLOWING,
		).toBeTruthy();
		expect(login.textContent).toContain(PURPOSE);
		// Nothing to save until something is typed, and nothing to remove.
		const save = within(login).getByRole("button", { name: "Save" });
		expect((save as HTMLButtonElement).disabled).toBe(true);
		expect(within(login).queryByRole("button", { name: "Remove" })).toBeNull();

		// Cancel closes it again.
		fireEvent.click(within(login).getByRole("button", { name: "Cancel" }));
		expect(within(login).queryByLabelText("Claude token")).toBeNull();
	});

	test("saving sends the token as today, empties and closes the field, and never shows it", async () => {
		const { saveLogin, container } = renderLogin(NONE);
		const login = await section();
		const field = open(login, "Give a token");

		fireEvent.change(field, { target: { value: TOKEN } });
		await act(async () => {
			fireEvent.click(within(login).getByRole("button", { name: "Save" }));
		});

		expect(saveLogin).toHaveBeenCalledWith(TOKEN);
		expect((field as HTMLInputElement).value).toBe("");
		expect(within(login).queryByLabelText("Claude token")).toBeNull();
		expect(
			within(login).getByText(`given ${day(GIVEN)} · due ${day(RENEW_BY)}`),
		).toBeTruthy();
		expect(within(login).getByText("Given")).toBeTruthy();
		expect(within(login).getByRole("button", { name: "Replace" })).toBeTruthy();
		expect(container.innerHTML).not.toContain(TOKEN.slice(10));
	});

	test("a token that is not a setup token is refused on the field, which stays open", async () => {
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
		const field = open(login, "Give a token");

		fireEvent.change(field, { target: { value: "sk-ant-api03-a-key" } });
		await act(async () => {
			fireEvent.click(within(login).getByRole("button", { name: "Save" }));
		});

		expect(login.textContent).toContain(NOT_A_TOKEN);
		expect(field.getAttribute("aria-invalid")).toBe("true");
		expect((field as HTMLInputElement).value).toBe("");
		expect(within(login).getByText("Not set")).toBeTruthy();
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

		fireEvent.change(open(login, "Give a token"), {
			target: { value: TOKEN },
		});
		await act(async () => {
			fireEvent.click(within(login).getByRole("button", { name: "Save" }));
		});

		expect(within(login).getByRole("alert").textContent).toBe(NOT_SAVED);
	});

	test("set, it shows given and due, with Replace, which opens the field", async () => {
		const { saveLogin } = renderLogin(at("ok"));
		const login = await section();

		expect(within(login).getByText("Given")).toBeTruthy();
		expect(
			within(login).getByText(`given ${day(GIVEN)} · due ${day(RENEW_BY)}`),
		).toBeTruthy();
		expect(login.textContent).not.toContain("Needs you");
		expect(within(login).queryByLabelText("Claude token")).toBeNull();
		const replace = within(login).getByRole("button", { name: "Replace" });
		expect(replace.className).toMatch(/ghost/);

		fireEvent.change(open(login, "Replace"), { target: { value: TOKEN } });
		await act(async () => {
			fireEvent.click(within(login).getByRole("button", { name: "Save" }));
		});
		expect(saveLogin).toHaveBeenCalledWith(TOKEN);
	});

	test("due within a month, it says Due in the warning style", async () => {
		renderLogin(at("due_soon"));
		const login = await section();

		expect(login.textContent).toContain("Needs you");
		expect(within(login).getByText("Due")).toBeTruthy();
		expect(
			within(login).getByText(`given ${day(GIVEN)} · due ${day(RENEW_BY)}`),
		).toBeTruthy();
		expect(within(login).getByRole("button", { name: "Replace" })).toBeTruthy();
	});

	test("expired, it says it ran out", async () => {
		renderLogin(at("expired"));
		const login = await section();

		expect(login.textContent).toContain("Needs you");
		expect(within(login).getByText("Expired")).toBeTruthy();
		expect(
			within(login).getByText(`given ${day(GIVEN)} · ran out ${day(RENEW_BY)}`),
		).toBeTruthy();
	});

	test("removing it asks first, behind Remove the Claude login?, and then it is not set", async () => {
		const { removeLogin } = renderLogin(at("ok"));
		const login = await section();
		open(login, "Replace");

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
			expect(within(login).getByText("Not set")).toBeTruthy(),
		);
		expect(
			within(login).getByRole("button", { name: "Give a token" }),
		).toBeTruthy();
	});

	test("cancelling the confirmation removes nothing", async () => {
		const { removeLogin } = renderLogin(at("ok"));
		const login = await section();
		open(login, "Replace");

		fireEvent.click(within(login).getByRole("button", { name: "Remove" }));
		fireEvent.click(
			within(screen.getByRole("dialog")).getByRole("button", {
				name: "Cancel",
			}),
		);

		expect(removeLogin).not.toHaveBeenCalled();
		expect(within(login).getByText("Given")).toBeTruthy();
	});

	test("a removal that fails says so and keeps the login", async () => {
		renderLogin(at("ok"), { removeLogin: () => Promise.reject(new Error()) });
		const login = await section();
		open(login, "Replace");

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
