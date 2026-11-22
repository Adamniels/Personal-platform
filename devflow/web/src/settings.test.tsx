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
	NOT_A_NUMBER,
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
};

/** What saving answers: the request, with the owner's login marked. */
const answered = (settings: SettingsRequest): ProjectSettings => ({
	pickup_label: settings.pickup_label ?? "",
	hold_label: settings.hold_label ?? "",
	attention_label: settings.attention_label ?? "",
	review_label: settings.review_label ?? "",
	waits_for_owner: settings.waits_for_owner ?? "",
	wait_when_marked: settings.wait_when_marked ?? false,
	wait_when_paths: settings.wait_when_paths ?? false,
	wait_when_decision: settings.wait_when_decision ?? false,
	wait_paths: (settings.wait_paths ?? []).map((pattern) => pattern ?? ""),
	trusted_logins: (settings.trusted_logins ?? []).map((login) => ({
		login,
		mine: login === "octo-owner",
	})),
	model_reading: settings.model_reading ?? "",
	model_building: settings.model_building ?? "",
	model_reviewing: settings.model_reviewing ?? "",
	run_minutes: settings.run_minutes ?? 0,
	run_turns: settings.run_turns ?? 0,
	fix_rounds_card: settings.fix_rounds_card ?? 0,
	fix_rounds_feature: settings.fix_rounds_feature ?? 0,
	questions: settings.questions ?? false,
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
	test("shows the models and limits and saves them with the rest", async () => {
		const { saveSettings } = renderPanel();
		const settings = await panel();
		const runs = within(
			settings.getByRole("region", { name: "Models and limits" }),
		);
		const value = (label: string) =>
			(runs.getByLabelText(label) as HTMLInputElement | HTMLSelectElement)
				.value;

		// A new project's: opus for each, 90 minutes, 200 turns, 3 rounds each.
		expect(
			[
				"Reading model",
				"Building model",
				"Reviewing model",
				"Minutes a run may take",
				"Turns a run may take",
				"Fix rounds a card gets",
				"Fix rounds a feature gets",
			].map(value),
		).toEqual(["opus", "opus", "opus", "90", "200", "3", "3"]);
		const building = runs.getByLabelText("Building model") as HTMLSelectElement;
		expect([...building.options].map((option) => option.textContent)).toEqual([
			"Opus",
			"Sonnet",
			"Haiku",
		]);

		fireEvent.change(building, { target: { value: "sonnet" } });
		fireEvent.change(runs.getByLabelText("Reviewing model"), {
			target: { value: "haiku" },
		});
		fireEvent.change(runs.getByLabelText("Minutes a run may take"), {
			target: { value: "45" },
		});
		fireEvent.change(runs.getByLabelText("Fix rounds a card gets"), {
			target: { value: "0" },
		});
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...DEFAULTS,
			model_building: "sonnet",
			model_reviewing: "haiku",
			run_minutes: 45,
			fix_rounds_card: 0,
			trusted_logins: ["octo-owner"],
		});
		expect(value("Fix rounds a card gets")).toBe("0");
	});

	test("a limit that is not a whole number is named, and nothing is sent", async () => {
		const { saveSettings } = renderPanel();
		const settings = await panel();
		const runs = within(
			settings.getByRole("region", { name: "Models and limits" }),
		);
		const turns = runs.getByLabelText(
			"Turns a run may take",
		) as HTMLInputElement;

		// Emptied while editing, it stays empty rather than becoming 0.
		fireEvent.change(turns, { target: { value: "" } });
		expect(turns.value).toBe("");
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(await runs.findByText(`Error: ${NOT_A_NUMBER}`)).toBeTruthy();
		expect(turns.getAttribute("aria-invalid")).toBe("true");
		expect(saveSettings).not.toHaveBeenCalled();
	});

	test("a limit Dev flow refuses is named under its field", async () => {
		renderPanel({
			saveSettings: () =>
				Promise.reject(
					new ApiError({
						status: 422,
						title: "The settings break a rule",
						detail: "run_minutes must be from 10 to 240",
						errors: { run_minutes: ["must be from 10 to 240"] },
					}),
				),
		});
		const settings = await panel();
		const minutes = settings.getByLabelText(
			"Minutes a run may take",
		) as HTMLInputElement;
		fireEvent.change(minutes, { target: { value: "300" } });
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(
			await settings.findByText("Error: must be from 10 to 240"),
		).toBeTruthy();
		expect(minutes.getAttribute("aria-invalid")).toBe("true");
		expect(minutes.value).toBe("300");
		expect(settings.queryByText(SAVED)).toBeNull();
	});

	test("sometimes shows its three conditions, the review label and the paths", async () => {
		const { saveSettings } = renderPanel({
			loadSettings: () =>
				Promise.resolve({ ...DEFAULTS, wait_paths: ["src/auth/**"] }),
		});
		const settings = await panel();
		const waits = (await settings.findByLabelText(
			"Pull requests wait for you",
		)) as HTMLSelectElement;

		// On never, nothing of sometimes is shown.
		expect(
			settings.queryByRole("group", { name: "Waits for you when" }),
		).toBeNull();
		expect(settings.queryByLabelText("Review label")).toBeNull();
		expect(settings.queryByLabelText("Paths")).toBeNull();

		fireEvent.change(waits, { target: { value: "sometimes" } });
		const conditions = within(
			settings.getByRole("group", { name: "Waits for you when" }),
		);
		const marked = conditions.getByLabelText(
			"The card or its feature is marked for your review",
		) as HTMLInputElement;
		const paths = conditions.getByLabelText(
			"The change touches a path on the list",
		) as HTMLInputElement;
		const decision = conditions.getByLabelText(
			"The agent made a decision for you",
		) as HTMLInputElement;
		expect([marked.checked, paths.checked, decision.checked]).toEqual([
			true,
			true,
			true,
		]);
		const review = conditions.getByLabelText(
			"Review label",
		) as HTMLInputElement;
		expect(review.value).toBe("devflow-review");
		const list = conditions.getByLabelText("Paths") as HTMLTextAreaElement;
		expect(list.value).toBe("src/auth/**");

		// Changed, and saved with the rest: one pattern a line, blank lines dropped.
		fireEvent.click(decision);
		fireEvent.change(review, { target: { value: "look at me" } });
		fireEvent.change(list, {
			target: { value: "src/auth/**\n\n  *.sql \n.github/\n" },
		});
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...DEFAULTS,
			waits_for_owner: "sometimes",
			review_label: "look at me",
			wait_when_decision: false,
			wait_paths: ["src/auth/**", "*.sql", ".github/"],
			trusted_logins: ["octo-owner"],
		});
		expect(
			(settings.getByLabelText("Paths") as HTMLTextAreaElement).value,
		).toBe("src/auth/**\n*.sql\n.github/");

		// Back on always, its values are kept and saved as they are.
		fireEvent.change(waits, { target: { value: "always" } });
		expect(settings.queryByLabelText("Paths")).toBeNull();
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		await waitFor(() =>
			expect(saveSettings).toHaveBeenLastCalledWith(ID, {
				...DEFAULTS,
				waits_for_owner: "always",
				review_label: "look at me",
				wait_when_decision: false,
				wait_paths: ["src/auth/**", "*.sql", ".github/"],
				trusted_logins: ["octo-owner"],
			}),
		);
	});

	test("a pattern or a condition Dev flow refuses is named under its field", async () => {
		renderPanel({
			loadSettings: () =>
				Promise.resolve({ ...DEFAULTS, waits_for_owner: "sometimes" }),
			saveSettings: () =>
				Promise.reject(
					new ApiError({
						status: 422,
						title: "The settings break a rule",
						detail: "wait_paths must not start with !",
						errors: {
							wait_paths: [
								"!src/** must not start with !: there is no negation",
							],
							review_label: ["must be a different label from the other three"],
						},
					}),
				),
		});
		const settings = await panel();
		fireEvent.click(await settings.findByRole("button", { name: "Save" }));

		const list = await settings.findByLabelText("Paths");
		await waitFor(() => expect(list.getAttribute("aria-invalid")).toBe("true"));
		expect(
			settings.getByText(
				"Error: !src/** must not start with !: there is no negation",
			),
		).toBeTruthy();
		expect(
			settings.getByLabelText("Review label").getAttribute("aria-invalid"),
		).toBe("true");
	});

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
			"sometimes",
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

	test("questions before starting is ticked, and saved off once unticked", async () => {
		const { saveSettings } = renderPanel();
		const settings = await panel();

		const questions = await settings.findByRole("checkbox", {
			name: "Questions before starting",
		});
		expect((questions as HTMLInputElement).checked).toBe(true);
		fireEvent.click(questions);
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...DEFAULTS,
			questions: false,
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
