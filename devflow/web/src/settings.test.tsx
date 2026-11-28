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
import type { Project } from "./projects";
import {
	NOT_A_NUMBER,
	NOT_DISCONNECTED,
	NOT_READ,
	NOT_SAVED,
	type ProjectSettings,
	presetOf,
	SAVED,
	SettingsPage,
	type SettingsRequest,
} from "./settings";
import type { ProjectValueList } from "./values";

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close, as in project.test.tsx.
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
	daily_token_budget: null,
	preset: "custom",
};

/** What saving the defaults sends: every setting, never the preset. */
const { preset: _, ...REQUEST } = DEFAULTS;

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
	daily_token_budget: settings.daily_token_budget,
	preset: presetOf({
		questions: settings.questions ?? false,
		waits_for_owner: settings.waits_for_owner ?? "",
		wait_when_marked: settings.wait_when_marked ?? false,
		wait_when_paths: settings.wait_when_paths ?? false,
		wait_when_decision: settings.wait_when_decision ?? false,
	}),
});

/** The page's form once it is drawn, as the panel's tests read it. */
const page = async () => {
	await screen.findByRole("button", { name: "Save" });
	return within(document.body);
};

describe("the settings page's form", () => {
	test("shows the models and limits and saves them with the rest", async () => {
		const { saveSettings } = renderPage();
		const settings = await page();
		const runs = within(settings.getByRole("article", { name: "Runs" }));
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
			...REQUEST,
			model_building: "sonnet",
			model_reviewing: "haiku",
			run_minutes: 45,
			fix_rounds_card: 0,
			trusted_logins: ["octo-owner"],
		});
		expect(value("Fix rounds a card gets")).toBe("0");
	});

	test("a limit that is not a whole number is named, and nothing is sent", async () => {
		const { saveSettings } = renderPage();
		const settings = await page();
		const runs = within(settings.getByRole("article", { name: "Runs" }));
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
		renderPage({
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
		const settings = await page();
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

	test("the daily budget is off, switched on with a number, and saved", async () => {
		const { saveSettings } = renderPage();
		const settings = await page();
		const usage = settings.getByRole("article", { name: "Usage" });
		expect(usage.id).toBe("usage");
		const card = within(usage);

		// Off by default, with no field.
		const daily = card.getByRole("switch", { name: "Daily budget" });
		expect((daily as HTMLInputElement).checked).toBe(false);
		expect(card.queryByLabelText("Million tokens a day")).toBeNull();

		// Switched on, the field shows, mono, with what counts as its help; the
		// switch alone is one change, and an empty field is named, not sent.
		fireEvent.click(daily);
		const millions = card.getByLabelText(
			"Million tokens a day",
		) as HTMLInputElement;
		expect(millions.className).toContain("mono");
		expect(
			card.getByText(
				"Counts input, cache writes and output. When reached, nothing new starts until midnight.",
			),
		).toBeTruthy();
		expect(settings.getByText("1 unsaved change")).toBeTruthy();
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		expect(await card.findByText(`Error: ${NOT_A_NUMBER}`)).toBeTruthy();
		expect(saveSettings).not.toHaveBeenCalled();

		// A number of millions is saved as tokens.
		fireEvent.change(millions, { target: { value: "25" } });
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...REQUEST,
			daily_token_budget: 25_000_000,
			trusted_logins: ["octo-owner"],
		});
		expect(millions.value).toBe("25");
		expect(settings.getByText("No unsaved changes")).toBeTruthy();

		// One Dev flow refuses is named in the field's own unit.
		saveSettings.mockImplementationOnce(() =>
			Promise.reject(
				new ApiError({
					status: 422,
					title: "The settings break a rule",
					detail: "daily_token_budget must be null for off",
					errors: { daily_token_budget: ["must be null for off"] },
				}),
			),
		);
		fireEvent.change(millions, { target: { value: "20000" } });
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		expect(
			await card.findByText("Error: must be a whole number from 1 to 10 000"),
		).toBeTruthy();

		// Switched off, it is saved as null.
		fireEvent.click(daily);
		expect(card.queryByLabelText("Million tokens a day")).toBeNull();
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		await waitFor(() =>
			expect(saveSettings).toHaveBeenLastCalledWith(ID, {
				...REQUEST,
				daily_token_budget: null,
				trusted_logins: ["octo-owner"],
			}),
		);
	});

	test("sometimes shows its three conditions, the review label and the paths", async () => {
		const { saveSettings } = renderPage({
			loadSettings: () =>
				Promise.resolve({ ...DEFAULTS, wait_paths: ["src/auth/**"] }),
		});
		const settings = await page();
		const waits = (await settings.findByLabelText(
			"Pull requests wait for you",
		)) as HTMLSelectElement;

		// On never, nothing of sometimes is shown; the review label is with the
		// other labels, under how cards are picked.
		expect(
			settings.queryByRole("group", { name: "Waits for you when" }),
		).toBeNull();
		expect(
			within(
				settings.getByRole("article", { name: "How cards are picked" }),
			).getByLabelText("Review label"),
		).toBeTruthy();
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
		const review = settings.getByLabelText("Review label") as HTMLInputElement;
		expect(review.value).toBe("devflow-review");
		const list = settings.getByLabelText("Paths") as HTMLTextAreaElement;
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
			...REQUEST,
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
				...REQUEST,
				waits_for_owner: "always",
				review_label: "look at me",
				wait_when_decision: false,
				wait_paths: ["src/auth/**", "*.sql", ".github/"],
				trusted_logins: ["octo-owner"],
			}),
		);
	});

	test("a pattern or a condition Dev flow refuses is named under its field", async () => {
		renderPage({
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
		const settings = await page();
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
		const { loadSettings } = renderPage();
		const settings = await page();

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
		const { saveSettings } = renderPage();
		const settings = await page();

		fireEvent.change(await settings.findByLabelText("Pick-up label"), {
			target: { value: "agent: go" },
		});
		fireEvent.change(settings.getByLabelText("Pull requests wait for you"), {
			target: { value: "always" },
		});
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...REQUEST,
			pickup_label: "agent: go",
			waits_for_owner: "always",
			trusted_logins: ["octo-owner"],
		});
	});

	test("questions before starting is ticked, and saved off once unticked", async () => {
		const { saveSettings } = renderPage();
		const settings = await page();

		const questions = await settings.findByRole("switch", {
			name: "Questions before starting",
		});
		expect((questions as HTMLInputElement).checked).toBe(true);
		fireEvent.click(questions);
		fireEvent.click(settings.getByRole("button", { name: "Save" }));

		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...REQUEST,
			questions: false,
			trusted_logins: ["octo-owner"],
		});
	});

	test("a value Dev flow refuses is named under its field", async () => {
		renderPage({
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
		const settings = await page();

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
		renderPage({
			saveSettings: () =>
				Promise.reject(
					new ApiError({ status: 0, title: "unreachable", detail: null }),
				),
		});
		const settings = await page();

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
		const { saveSettings } = renderPage({
			loadSettings: () =>
				Promise.resolve({
					...DEFAULTS,
					trusted_logins: [
						{ login: "octo-owner", mine: true },
						{ login: "octo-friend", mine: false },
					],
				}),
		});
		const settings = await page();
		const listed = () =>
			within(settings.getByRole("list", { name: "Trusted accounts" }))
				.getAllByRole("listitem")
				.map((item) => item.textContent);

		// Mine first, marked, with nothing to remove it by; the others each with
		// a way to remove them.
		expect(
			await settings.findByRole("list", { name: "Trusted accounts" }),
		).toBeTruthy();
		expect(listed()).toEqual(["octo-owneryou, always on", "octo-friendRemove"]);
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
			"octo-owneryou, always on",
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
			"octo-owneryou, always on",
			"octo-palRemove",
			"octo-buddyRemove",
		]);
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...REQUEST,
			trusted_logins: ["octo-owner", "octo-pal", "octo-buddy"],
		});
	});

	test("a login Dev flow refuses is named under the field", async () => {
		renderPage({
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
		const settings = await page();

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

	test("a preset fills in what it covers and leaves the rest", async () => {
		const { saveSettings } = renderPage({
			loadSettings: () =>
				Promise.resolve({
					...DEFAULTS,
					waits_for_owner: "always",
					wait_when_paths: false,
					preset: "only_i_merge",
				}),
		});
		const settings = await page();
		const alone = within(
			settings.getByRole("article", { name: "How much happens alone" }),
		);
		const summary = within(
			settings.getByRole("article", { name: "These settings" }),
		);
		expect(summary.getByText("Only I merge")).toBeTruthy();

		// Typed before choosing, and kept after.
		fireEvent.change(settings.getByLabelText("Pick-up label"), {
			target: { value: "agent: go" },
		});
		fireEvent.change(settings.getByLabelText("Minutes a run may take"), {
			target: { value: "45" },
		});
		const guided = alone.getByRole("radio", {
			name: "Guided",
		}) as HTMLInputElement;
		expect(guided.checked).toBe(false);
		fireEvent.click(guided);

		expect(guided.checked).toBe(true);
		expect(summary.getByText("Guided")).toBeTruthy();
		expect(
			(alone.getByLabelText("Pull requests wait for you") as HTMLSelectElement)
				.value,
		).toBe("sometimes");
		expect(
			(
				alone.getByRole("switch", {
					name: "Questions before starting",
				}) as HTMLInputElement
			).checked,
		).toBe(false);
		const conditions = within(
			alone.getByRole("group", { name: "Waits for you when" }),
		);
		for (const condition of [
			"The card or its feature is marked for your review",
			"The change touches a path on the list",
			"The agent made a decision for you",
		]) {
			expect(
				(conditions.getByLabelText(condition) as HTMLInputElement).checked,
			).toBe(true);
		}
		expect(
			(settings.getByLabelText("Pick-up label") as HTMLInputElement).value,
		).toBe("agent: go");
		expect(
			(settings.getByLabelText("Minutes a run may take") as HTMLInputElement)
				.value,
		).toBe("45");

		// Nothing is saved until Save, and then as any change, with no preset.
		expect(saveSettings).not.toHaveBeenCalled();
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		expect(await settings.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...REQUEST,
			pickup_label: "agent: go",
			run_minutes: 45,
			questions: false,
			waits_for_owner: "sometimes",
			wait_when_marked: true,
			wait_when_paths: true,
			wait_when_decision: true,
			trusted_logins: ["octo-owner"],
		});
		expect(summary.getByText("Guided")).toBeTruthy();
	});

	test("the name follows the settings", async () => {
		renderPage({
			loadSettings: () =>
				Promise.resolve({
					...DEFAULTS,
					waits_for_owner: "always",
					preset: "only_i_merge",
				}),
		});
		const settings = await page();
		const alone = within(
			settings.getByRole("article", { name: "How much happens alone" }),
		);
		const summary = within(
			settings.getByRole("article", { name: "These settings" }),
		);
		const chosen = () =>
			["Fully automatic", "Guided", "Only I merge"].filter(
				(name) =>
					(alone.getByRole("radio", { name }) as HTMLInputElement).checked,
			);
		expect(summary.getByText("Only I merge")).toBeTruthy();
		expect(chosen()).toEqual(["Only I merge"]);

		// One covered setting changed by hand matches no preset.
		fireEvent.click(
			alone.getByRole("switch", { name: "Questions before starting" }),
		);
		expect(summary.getByText("Custom")).toBeTruthy();
		expect(chosen()).toEqual([]);

		fireEvent.click(alone.getByRole("radio", { name: "Fully automatic" }));
		expect(summary.getByText("Fully automatic")).toBeTruthy();
		expect(chosen()).toEqual(["Fully automatic"]);

		// On sometimes, every condition counts.
		fireEvent.click(alone.getByRole("radio", { name: "Guided" }));
		fireEvent.click(
			alone.getByLabelText("The change touches a path on the list"),
		);
		expect(summary.getByText("Custom")).toBeTruthy();
	});

	test("settings that could not be read say so", async () => {
		renderPage({ loadSettings: () => Promise.reject(new Error("down")) });

		expect((await screen.findByRole("alert")).textContent).toBe(NOT_READ);
		expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
	});
});

const PLATFORM: Project = {
	id: ID,
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

/** The page, with every call a mock answering as Dev flow would: the
 * settings as given, saved as sent, and the values kept in a list a give
 * and a remove change. */
function renderPage(
	api: {
		loadSettings?: (id: string) => Promise<ProjectSettings>;
		saveSettings?: (
			id: string,
			settings: SettingsRequest,
		) => Promise<ProjectSettings>;
		values?: ProjectValueList["values"];
	} = {},
) {
	let values = api.values ?? [];
	const calls = {
		loadProjects: vi.fn(() => Promise.resolve([PLATFORM])),
		loadSettings: vi.fn(api.loadSettings ?? (() => Promise.resolve(DEFAULTS))),
		saveSettings: vi.fn(
			api.saveSettings ??
				((_: string, settings: SettingsRequest) =>
					Promise.resolve(answered(settings))),
		),
		loadValues: vi.fn(() => Promise.resolve({ done: true, values })),
		giveValue: vi.fn(async (_: string, name: string, _value: string) => {
			values = [
				...values.filter((value) => value.name !== name),
				{
					name,
					asked: false,
					why: null,
					problem: null,
					set_at: "2026-11-24T10:00:00Z",
				},
			];
		}),
		removeValue: vi.fn(async (_: string, name: string) => {
			values = values.filter((value) => value.name !== name);
		}),
		loadSchedules: vi.fn(() =>
			Promise.resolve({
				runs_by: "always",
				time_zone: "Europe/Stockholm",
				schedules: [],
				next_start: null,
				running_until: null,
			}),
		),
		saveSchedules: vi.fn(() => new Promise<never>(() => {})),
		saveTimeZone: vi.fn(() => new Promise<never>(() => {})),
		disconnect: vi.fn(() => Promise.resolve()),
		back: vi.fn(),
		disconnected: vi.fn(),
	};
	render(<SettingsPage id={ID} {...calls} />);
	return calls;
}

const section = async (name: string) =>
	within(await screen.findByRole("article", { name }));

const shown = (element: HTMLElement) =>
	(element as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value;

describe("the settings page", () => {
	test("the page shows the four sections and saves once", async () => {
		const { saveSettings } = renderPage({
			loadSettings: () =>
				Promise.resolve({
					...DEFAULTS,
					waits_for_owner: "sometimes",
					wait_paths: ["src/auth/**"],
					trusted_logins: [
						{ login: "octo-friend", mine: false },
						{ login: "octo-owner", mine: true },
					],
				}),
		});

		// The header: the way back to the project, its repository, the title
		// and what the page is for.
		expect(
			await screen.findByRole("heading", { name: "Settings" }),
		).toBeTruthy();
		expect(
			screen.getByRole("link", { name: "Personal-platform" }),
		).toBeTruthy();
		expect(screen.getByText("Adamniels/Personal-platform")).toBeTruthy();

		// The four sections, in order.
		const names = [
			"How much happens alone",
			"How cards are picked",
			"Runs",
			"Values",
		];
		const articles = await Promise.all(
			names.map((name) => screen.findByRole("article", { name })),
		);
		for (const [at, article] of articles.slice(1).entries()) {
			expect(
				(articles[at] as HTMLElement).compareDocumentPosition(article) &
					Node.DOCUMENT_POSITION_FOLLOWING,
			).toBeTruthy();
		}

		// All eighteen settings, grouped: six alone, five picked, seven runs.
		const alone = await section("How much happens alone");
		expect(
			alone.getByRole("switch", { name: "Questions before starting" }),
		).toBeTruthy();
		expect(shown(alone.getByLabelText("Pull requests wait for you"))).toBe(
			"sometimes",
		);
		expect(
			within(alone.getByRole("group", { name: "Waits for you when" }))
				.getAllByRole("checkbox")
				.map((box) => (box as HTMLInputElement).checked),
		).toEqual([true, true, true]);
		expect(shown(alone.getByLabelText("Paths"))).toBe("src/auth/**");

		const picked = await section("How cards are picked");
		expect(
			["Pick-up label", "Hold label", "Attention label", "Review label"].map(
				(label) => shown(picked.getByLabelText(label)),
			),
		).toEqual([
			"devflow",
			"devflow-hold",
			"devflow-attention",
			"devflow-review",
		]);
		// Mine first, always on, with nothing to remove it by.
		const trusted = within(
			picked.getByRole("list", { name: "Trusted accounts" }),
		).getAllByRole("listitem");
		expect(trusted.map((row) => row.textContent)).toEqual([
			"octo-owneryou, always on",
			"octo-friendRemove",
		]);
		expect(
			picked.queryByRole("button", { name: "Remove octo-owner" }),
		).toBeNull();

		const runs = await section("Runs");
		expect(
			[
				"Reading model",
				"Building model",
				"Reviewing model",
				"Minutes a run may take",
				"Turns a run may take",
				"Fix rounds a card gets",
				"Fix rounds a feature gets",
			].map((label) => shown(runs.getByLabelText(label))),
		).toEqual(["opus", "opus", "opus", "90", "200", "3", "3"]);
		expect(runs.getByText("A build, a fix, a conflict.")).toBeTruthy();

		// Nothing changed: Undo is off.
		const undo = screen.getByRole("button", { name: "Undo changes" });
		const save = screen.getByRole("button", { name: "Save" });
		expect(screen.getByText("No unsaved changes")).toBeTruthy();
		expect((undo as HTMLButtonElement).disabled).toBe(true);

		// Two changes are counted, and Undo puts the form back as read.
		fireEvent.change(picked.getByLabelText("Pick-up label"), {
			target: { value: "agent: go" },
		});
		fireEvent.change(runs.getByLabelText("Minutes a run may take"), {
			target: { value: "45" },
		});
		expect(screen.getByText("2 unsaved changes")).toBeTruthy();
		fireEvent.click(undo);
		expect(shown(picked.getByLabelText("Pick-up label"))).toBe("devflow");
		expect(shown(runs.getByLabelText("Minutes a run may take"))).toBe("90");
		expect(screen.getByText("No unsaved changes")).toBeTruthy();
		expect((undo as HTMLButtonElement).disabled).toBe(true);

		// Saved in one PUT with every setting.
		fireEvent.change(picked.getByLabelText("Pick-up label"), {
			target: { value: "agent: go" },
		});
		fireEvent.change(alone.getByLabelText("Pull requests wait for you"), {
			target: { value: "always" },
		});
		expect(screen.getByText("2 unsaved changes")).toBeTruthy();
		fireEvent.click(save);
		expect(await screen.findByText(SAVED)).toBeTruthy();
		expect(saveSettings).toHaveBeenCalledTimes(1);
		expect(saveSettings).toHaveBeenCalledWith(ID, {
			...REQUEST,
			pickup_label: "agent: go",
			waits_for_owner: "always",
			wait_paths: ["src/auth/**"],
			trusted_logins: ["octo-friend", "octo-owner"],
		});
		expect(screen.getByText("No unsaved changes")).toBeTruthy();

		// Undo now goes back to the settings as saved, not as first read.
		fireEvent.change(picked.getByLabelText("Hold label"), {
			target: { value: "wait" },
		});
		expect(screen.getByText("1 unsaved change")).toBeTruthy();
		fireEvent.click(undo);
		expect(shown(picked.getByLabelText("Hold label"))).toBe("devflow-hold");
		expect(shown(picked.getByLabelText("Pick-up label"))).toBe("agent: go");
	});

	test("the preset is a segmented choice and the summary names the match", async () => {
		renderPage();
		const alone = await section("How much happens alone");
		const summary = await section("These settings");
		const preset = within(alone.getByRole("radiogroup", { name: "Preset" }));
		const checked = () =>
			preset
				.getAllByRole("radio")
				.filter((radio) => (radio as HTMLInputElement).checked)
				.map((radio) => radio.getAttribute("value"));

		// Questions on and never matches no preset: none is chosen.
		expect(
			preset.getAllByRole("radio").map((radio) => radio.getAttribute("value")),
		).toEqual(["fully_automatic", "guided", "only_i_merge"]);
		expect(checked()).toEqual([]);
		expect(summary.getByText("Custom")).toBeTruthy();
		// Paths and the conditions show only on sometimes.
		expect(alone.queryByLabelText("Paths")).toBeNull();
		expect(
			alone.queryByRole("group", { name: "Waits for you when" }),
		).toBeNull();

		// Guided fills its five settings.
		fireEvent.click(preset.getByRole("radio", { name: "Guided" }));
		expect(checked()).toEqual(["guided"]);
		expect(
			(
				alone.getByRole("switch", {
					name: "Questions before starting",
				}) as HTMLInputElement
			).checked,
		).toBe(false);
		expect(shown(alone.getByLabelText("Pull requests wait for you"))).toBe(
			"sometimes",
		);
		expect(
			within(alone.getByRole("group", { name: "Waits for you when" }))
				.getAllByRole("checkbox")
				.map((box) => (box as HTMLInputElement).checked),
		).toEqual([true, true, true]);
		expect(alone.getByLabelText("Paths")).toBeTruthy();
		expect(summary.getByText("Guided")).toBeTruthy();

		// One changed by hand matches none: Custom, and no segment chosen.
		fireEvent.click(
			alone.getByLabelText("The change touches a path on the list"),
		);
		expect(checked()).toEqual([]);
		expect(summary.getByText("Custom")).toBeTruthy();

		fireEvent.click(preset.getByRole("radio", { name: "Fully automatic" }));
		expect(checked()).toEqual(["fully_automatic"]);
		expect(summary.getByText("Fully automatic")).toBeTruthy();
		expect(alone.queryByLabelText("Paths")).toBeNull();
	});

	test("values are listed by name with Replace, Remove and Add a value", async () => {
		const { giveValue, removeValue, saveSettings } = renderPage({
			values: [
				{
					name: "FODR_TEST_SMTP_PASSWORD",
					asked: true,
					why: "The mail tests log in to the test SMTP server.",
					problem: null,
					set_at: "2026-11-20T09:30:00Z",
				},
			],
		});
		const values = await section("Values");
		const list = within(await values.findByRole("list", { name: "Values" }));
		const row = within(list.getByRole("listitem"));
		expect(row.getByText("FODR_TEST_SMTP_PASSWORD")).toBeTruthy();
		expect(row.getByText(/^set /)).toBeTruthy();

		// Replaced: the value is typed as a password, sent once, and gone.
		fireEvent.click(
			row.getByRole("button", { name: "Replace FODR_TEST_SMTP_PASSWORD" }),
		);
		const field = row.getByLabelText("The value of FODR_TEST_SMTP_PASSWORD");
		expect(field.getAttribute("type")).toBe("password");
		fireEvent.change(field, { target: { value: "s3cret-one" } });
		fireEvent.click(row.getByRole("button", { name: "Give" }));
		await waitFor(() =>
			expect(giveValue).toHaveBeenCalledWith(
				ID,
				"FODR_TEST_SMTP_PASSWORD",
				"s3cret-one",
			),
		);
		await waitFor(() =>
			expect(
				values.queryByLabelText("The value of FODR_TEST_SMTP_PASSWORD"),
			).toBeNull(),
		);
		expect(screen.queryByDisplayValue("s3cret-one")).toBeNull();
		expect(document.body.textContent).not.toContain("s3cret-one");

		// Added: a name and a password field.
		fireEvent.click(values.getByRole("button", { name: "Add a value" }));
		const adding = within(values.getByRole("form", { name: "Add a value" }));
		fireEvent.change(adding.getByLabelText("Its name"), {
			target: { value: "FODR_API_KEY" },
		});
		const secret = adding.getByLabelText("The value");
		expect(secret.getAttribute("type")).toBe("password");
		fireEvent.change(secret, { target: { value: "k3y-two" } });
		fireEvent.click(adding.getByRole("button", { name: "Give" }));
		expect(await values.findByText("FODR_API_KEY")).toBeTruthy();
		expect(giveValue).toHaveBeenCalledWith(ID, "FODR_API_KEY", "k3y-two");
		expect(document.body.textContent).not.toContain("k3y-two");

		// Removed at once, never through Save.
		fireEvent.click(
			values.getByRole("button", { name: "Remove FODR_TEST_SMTP_PASSWORD" }),
		);
		await waitFor(() =>
			expect(values.queryByText("FODR_TEST_SMTP_PASSWORD")).toBeNull(),
		);
		expect(removeValue).toHaveBeenCalledWith(ID, "FODR_TEST_SMTP_PASSWORD");
		expect(saveSettings).not.toHaveBeenCalled();
		expect(screen.getByText("No unsaved changes")).toBeTruthy();
	});
	test("Disconnect sits in the side column behind its confirmation", async () => {
		const { disconnect, disconnected } = renderPage();
		const leave = await screen.findByRole("button", {
			name: "Disconnect Personal-platform",
		});

		// Cancelled, nothing happens.
		fireEvent.click(leave);
		const dialog = within(
			screen.getByRole("dialog", {
				name: "Disconnect Adamniels/Personal-platform?",
			}),
		);
		expect(dialog.getByText(/Nothing changes on GitHub\./)).toBeTruthy();
		fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));
		expect(disconnect).not.toHaveBeenCalled();

		// Confirmed, the project is disconnected and the page goes to the tab.
		fireEvent.click(leave);
		fireEvent.click(dialog.getByRole("button", { name: "Disconnect" }));
		await waitFor(() => expect(disconnected).toHaveBeenCalledOnce());
		expect(disconnect).toHaveBeenCalledWith(ID);
	});

	test("a disconnect that failed says so, and stays", async () => {
		const { disconnect, disconnected } = renderPage();
		disconnect.mockImplementation(() => Promise.reject(new Error("down")));

		fireEvent.click(
			await screen.findByRole("button", {
				name: "Disconnect Personal-platform",
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
		expect(disconnected).not.toHaveBeenCalled();
	});

	test("a project the caller does not have is not found", async () => {
		render(
			<SettingsPage
				id="0190a8f0-0000-7000-8000-0000000000ff"
				loadProjects={() => Promise.resolve([PLATFORM])}
				loadSettings={() => Promise.resolve(DEFAULTS)}
				saveSettings={() => new Promise(() => {})}
				loadValues={() => new Promise(() => {})}
				loadSchedules={() => new Promise(() => {})}
				saveSchedules={() => new Promise(() => {})}
				saveTimeZone={() => new Promise(() => {})}
				giveValue={() => Promise.resolve()}
				removeValue={() => Promise.resolve()}
				disconnect={() => Promise.resolve()}
				back={() => {}}
				disconnected={() => {}}
			/>,
		);

		expect(await screen.findByText("There is no such project")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
	});
});
