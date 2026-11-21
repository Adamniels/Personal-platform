import {
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
	NOT_READ,
	NOT_SAVED,
	type ProjectSettings,
	SAVED,
	SettingsPanel,
	type SettingsRequest,
} from "./settings";

afterEach(cleanup);

const ID = "0190a8f0-0000-7000-8000-000000000001";

const DEFAULTS: ProjectSettings = {
	pickup_label: "devflow",
	hold_label: "devflow-hold",
	attention_label: "devflow-attention",
	waits_for_owner: "never",
	trusted_logins: [{ login: "octo-owner", mine: true }],
};

/** What saving answers: the request, with the owner's login marked. */
const answered = (settings: SettingsRequest): ProjectSettings => ({
	pickup_label: settings.pickup_label ?? "",
	hold_label: settings.hold_label ?? "",
	attention_label: settings.attention_label ?? "",
	waits_for_owner: settings.waits_for_owner ?? "",
	trusted_logins: (settings.trusted_logins ?? []).map((login) => ({
		login,
		mine: login === "octo-owner",
	})),
});

function renderPanel(
	api: {
		loadSettings?: (id: string) => Promise<ProjectSettings>;
		saveSettings?: (
			id: string,
			settings: SettingsRequest,
		) => Promise<ProjectSettings>;
	} = {},
) {
	const calls = {
		loadSettings: vi.fn(api.loadSettings ?? (() => Promise.resolve(DEFAULTS))),
		saveSettings: vi.fn(
			api.saveSettings ??
				((_: string, settings: SettingsRequest) =>
					Promise.resolve(answered(settings))),
		),
	};
	render(<SettingsPanel id={ID} {...calls} />);
	return calls;
}

const panel = async () =>
	within(await screen.findByRole("article", { name: "Settings" }));

describe("the settings panel", () => {
	test("shows the project's settings", async () => {
		const { loadSettings } = renderPanel();
		const settings = await panel();

		expect(loadSettings).toHaveBeenCalledWith(ID);
		expect(
			((await settings.findByLabelText("Pick-up label")) as HTMLInputElement)
				.value,
		).toBe("devflow");
		expect(
			(settings.getByLabelText("Hold label") as HTMLInputElement).value,
		).toBe("devflow-hold");
		expect(
			(settings.getByLabelText("Attention label") as HTMLInputElement).value,
		).toBe("devflow-attention");
		const waits = settings.getByLabelText(
			"Pull requests wait for you",
		) as HTMLSelectElement;
		expect(waits.value).toBe("never");
		expect([...waits.options].map((option) => option.value)).toEqual([
			"never",
			"always",
		]);
	});

	test("saves all five, and says so", async () => {
		const { saveSettings } = renderPanel();
		const settings = await panel();

		fireEvent.change(await settings.findByLabelText("Pick-up label"), {
			target: { value: "agent: go" },
		});
		fireEvent.change(settings.getByLabelText("Pull requests wait for you"), {
			target: { value: "always" },
		});
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...DEFAULTS,
			pickup_label: "agent: go",
			waits_for_owner: "always",
			trusted_logins: ["octo-owner"],
		});
	});

	test("a value Dev flow refuses is named under its field", async () => {
		renderPanel({
			saveSettings: () =>
				Promise.reject(
					new ApiError({
						status: 422,
						title: "The settings break a rule",
						detail: "hold_label must be a different label from the other two",
						errors: {
							hold_label: ["must be a different label from the other two"],
						},
					}),
				),
		});
		const settings = await panel();

		fireEvent.change(await settings.findByLabelText("Hold label"), {
			target: { value: "devflow" },
		});
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		const hold = await settings.findByLabelText("Hold label");
		await waitFor(() => expect(hold.getAttribute("aria-invalid")).toBe("true"));
		expect(
			settings.getByText("Error: must be a different label from the other two"),
		).toBeTruthy();
		expect(
			settings.getByLabelText("Pick-up label").getAttribute("aria-invalid"),
		).not.toBe("true");
		expect(settings.queryByText(SAVED)).toBeNull();
	});

	test("a save that failed otherwise says so, and keeps what was typed", async () => {
		renderPanel({
			saveSettings: () =>
				Promise.reject(
					new ApiError({ status: 0, title: "unreachable", detail: null }),
				),
		});
		const settings = await panel();

		fireEvent.change(await settings.findByLabelText("Attention label"), {
			target: { value: "look" },
		});
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect((await settings.findByRole("alert")).textContent).toBe(NOT_SAVED);
		expect(
			(settings.getByLabelText("Attention label") as HTMLInputElement).value,
		).toBe("look");
	});

	test("trusted accounts are listed, added and removed, and my own cannot be removed", async () => {
		const { saveSettings } = renderPanel({
			loadSettings: () =>
				Promise.resolve({
					...DEFAULTS,
					trusted_logins: [
						{ login: "octo-owner", mine: true },
						{ login: "octo-friend", mine: false },
					],
				}),
		});
		const settings = await panel();
		const listed = () =>
			within(settings.getByRole("list", { name: "Trusted accounts" }))
				.getAllByRole("listitem")
				.map((item) => item.textContent);

		// Mine first, marked, with nothing to remove it by; the others each with
		// a way to remove them.
		expect(
			await settings.findByRole("list", { name: "Trusted accounts" }),
		).toBeTruthy();
		expect(listed()).toEqual(["octo-ownerYou", "octo-friendRemove"]);
		expect(
			settings.queryByRole("button", { name: "Remove octo-owner" }),
		).toBeNull();

		// Added from the field, by the button or by Enter, and a login already
		// listed whatever its case is not listed twice.
		const add = settings.getByLabelText("Add a trusted account");
		fireEvent.change(add, { target: { value: " octo-pal " } });
		fireEvent.click(settings.getByRole("button", { name: "Add" }));
		expect((add as HTMLInputElement).value).toBe("");
		fireEvent.change(add, { target: { value: "octo-buddy" } });
		fireEvent.keyDown(add, { key: "Enter" });
		fireEvent.change(add, { target: { value: "Octo-Friend" } });
		fireEvent.click(settings.getByRole("button", { name: "Add" }));
		expect(listed()).toEqual([
			"octo-ownerYou",
			"octo-friendRemove",
			"octo-palRemove",
			"octo-buddyRemove",
		]);
		expect(saveSettings).not.toHaveBeenCalled();

		// Removed, and saved with the rest, mine among them.
		fireEvent.click(
			settings.getByRole("button", { name: "Remove octo-friend" }),
		);
		expect(listed()).toEqual([
			"octo-ownerYou",
			"octo-palRemove",
			"octo-buddyRemove",
		]);
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...DEFAULTS,
			trusted_logins: ["octo-owner", "octo-pal", "octo-buddy"],
		});
	});

	test("a login Dev flow refuses is named under the field", async () => {
		renderPanel({
			saveSettings: () =>
				Promise.reject(
					new ApiError({
						status: 422,
						title: "The settings break a rule",
						detail: "trusted_logins must hold GitHub logins only",
						errors: { trusted_logins: ["must hold GitHub logins only"] },
					}),
				),
		});
		const settings = await panel();

		fireEvent.change(await settings.findByLabelText("Add a trusted account"), {
			target: { value: "not a login" },
		});
		fireEvent.click(settings.getByRole("button", { name: "Add" }));
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		const add = settings.getByLabelText("Add a trusted account");
		await waitFor(() => expect(add.getAttribute("aria-invalid")).toBe("true"));
		expect(
			settings.getByText("Error: must hold GitHub logins only"),
		).toBeTruthy();
	});

	test("settings that could not be read say so", async () => {
		renderPanel({ loadSettings: () => Promise.reject(new Error("down")) });

		expect((await (await panel()).findByRole("alert")).textContent).toBe(
			NOT_READ,
		);
	});
});
