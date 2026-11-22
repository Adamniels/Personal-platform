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
import type { ProjectSettings, SettingsRequest } from "./settings";
import {
	CHECK_RUNNING,
	COMMENT_ON_PULL,
	COMMENT_RUNS_AGAIN,
	NO_DEVFLOW,
	NOT_ACCEPTED,
	type ProjectSetup,
	RUN_SETUP_AGAIN,
	SEND_BACK,
	SEND_BACK_LABEL,
	SENT_BACK_RUN,
	SETTING_UP,
	SETTINGS_PROPOSED,
	SETTINGS_SAVE_ONCE,
	type SetupCheck,
} from "./setup";

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

/** The command that starts the project's setup worker. */
const SETUP_COMMAND =
	"{ docker rm -f devflow-fodr-setup 2>/dev/null; docker run -d --name devflow-fodr-setup --memory 2g --env-file ~/.devflow/fodr.env -e DEVFLOW_URL=http://host.docker.internal:9080 -e DEVFLOW_BASE_IMAGE=devflow-worker:dev devflow-worker:dev; }";

/** A project registered, every step done, with no check to show. */
const REGISTERED: ProjectSetup = {
	state: "registered",
	steps: [
		{ step: "setup", done: true },
		{ step: "recipe", done: true },
		{ step: "settings", done: true },
		{ step: "worker", done: true },
		{ step: "check", done: true },
	],
	check: null,
	checking: false,
	problem: null,
	setup_command: SETUP_COMMAND,
	proposal: null,
	can_send_back: false,
	settings_waits: false,
	setup: null,
};

function renderPage(
	props: Partial<ProjectPageProps> & {
		list?: Partial<WorkerList>;
		setup?: ProjectSetup;
	} = {},
) {
	let list: WorkerList = { ...EMPTY, ...props.list };
	let setup: ProjectSetup = props.setup ?? REGISTERED;
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
				preset: "custom" as const,
			}),
		),
		saveSettings: vi.fn(
			(_id: string, _settings: SettingsRequest): Promise<ProjectSettings> =>
				Promise.reject(new Error("the project page's tests do not save")),
		),
		loadCards: vi.fn(() => Promise.resolve({ cards: [], features: [] })),
		loadSetup: vi.fn(() => Promise.resolve(setup)),
		mergeSetup: vi.fn(() => Promise.resolve()),
		runSetupAgain: vi.fn(() => Promise.resolve()),
		sendSetupBack: vi.fn((_id: string, _comment: string) => Promise.resolve()),
		checkAgain: vi.fn(() => {
			if (setup.check)
				setup = { ...setup, check: { ...setup.check, again_asked: true } };
			return Promise.resolve();
		}),
		copy: vi.fn(() => Promise.resolve()),
		back: vi.fn(),
	};
	const { list: _, setup: __, ...rest } = props;
	render(<ProjectPage id={ID} now={() => NOW} {...api} {...rest} />);
	return api;
}

const panel = () => screen.findByRole("article", { name: "Workers" });

describe("a project's setup", () => {
	const failedCheck: SetupCheck = {
		id: "0190a8f0-0000-7000-8000-00000000c4e1",
		version: "4f6c1c36b0bd2c24e3d5a8f5d8c0e0a3a0e3b9a1",
		commit: "9a1b2c3d4e5f60718293a4b5c6d7e8f901234567",
		passed: false,
		exit_code: 1,
		output: "npm test\nFAIL src/list.test.ts\nTests: 1 failed, 11 passed",
		pictures: [],
		pictures_note: null,
		checked_at: "2026-11-17T11:40:00+00:00",
		again_asked: false,
	};

	const failed: ProjectSetup = {
		state: "setting_up",
		steps: [
			{ step: "setup", done: true },
			{ step: "recipe", done: true },
			{ step: "settings", done: true },
			{ step: "worker", done: true },
			{ step: "check", done: false },
		],
		check: failedCheck,
		checking: false,
		problem: null,
		setup_command: SETUP_COMMAND,
		proposal: null,
		can_send_back: false,
		settings_waits: false,
		setup: null,
	};

	const HEAD = "9b1c0de4f6c1c36b0bd2c24e3d5a8f5d8c0e0a3a";

	/** A project with no .devflow/, its setup run approved and waiting. */
	const waiting: ProjectSetup = {
		state: "setting_up",
		steps: [
			{ step: "setup", done: false },
			{ step: "recipe", done: false },
			{ step: "settings", done: true },
			{ step: "worker", done: false },
			{ step: "check", done: false },
		],
		check: null,
		checking: false,
		problem: ".devflow/Dockerfile is missing.",
		setup_command: SETUP_COMMAND,
		proposal: null,
		can_send_back: false,
		settings_waits: false,
		setup: {
			number: 1,
			branch: "devflow/setup",
			sent_back: false,
			state: "waiting_for_owner",
			reason: "Approved; it waits for you to merge it",
			pull_request_number: 7,
			pull_request_url: "https://github.com/Adamniels/fodr/pull/7",
			running: false,
			head_commit: HEAD,
			report: {
				summary: "Wrote a recipe with Postgres, and a check that starts it.",
				proposed: { preset: "guided", why: "Its migrations.", paths: [] },
				variables: [
					{ name: "STRIPE_TEST_KEY", why: "The payment tests call Stripe." },
				],
				unsettled: [
					{
						kind: "real_secret",
						detail: "The export tests upload to the real bucket.",
					},
					{
						kind: "containers_in_tests",
						detail: "The integration tests start Redis.",
					},
				],
				decisions: [],
				handover: "",
			},
			review: {
				verdict: "approve",
				summary: "The recipe installs only Postgres.",
				blocking: 0,
				commit: HEAD,
			},
		},
	};

	test("a project without .devflow/ shows its setup run, its command and what it could not settle", async () => {
		const { mergeSetup, copy } = renderPage({ setup: waiting });

		const panel = await screen.findByRole("article", { name: "Setting up" });
		const run = within(panel).getByRole("region", { name: "The setup run" });
		// Why it is due, and the command that starts its worker, to copy.
		expect(
			within(run).getByText(".devflow/Dockerfile is missing."),
		).toBeTruthy();
		expect(within(run).getByText(SETUP_COMMAND)).toBeTruthy();
		fireEvent.click(
			within(run).getByRole("button", { name: "Copy the command" }),
		);
		expect(copy).toHaveBeenCalledWith(SETUP_COMMAND);
		// Where it is, its review, and its pull request.
		expect(within(run).getByText("Waits for you")).toBeTruthy();
		expect(
			within(run).getByText("Approved; it waits for you to merge it."),
		).toBeTruthy();
		expect(
			within(run).getByText("The recipe installs only Postgres."),
		).toBeTruthy();
		expect(
			within(run)
				.getByRole("link", { name: "Pull request #7" })
				.getAttribute("href"),
		).toBe("https://github.com/Adamniels/fodr/pull/7");
		// What it could not settle, each by its kind.
		const unsettled = within(run).getByRole("list", {
			name: "What it could not settle",
		});
		expect(
			within(unsettled)
				.getAllByRole("listitem")
				.map((item) => item.textContent),
		).toEqual([
			"Needs a real secretThe export tests upload to the real bucket.",
			"Its tests start containersThe integration tests start Redis.",
		]);
		expect(within(run).getByText("STRIPE_TEST_KEY")).toBeTruthy();
		// Merged from the step as the owner, at the commit shown.
		fireEvent.click(within(run).getByRole("button", { name: "Merge" }));
		await waitFor(() => expect(mergeSetup).toHaveBeenCalledWith(ID, HEAD));
	});

	/** The setup stopped before it had a pull request, twice in a row. */
	const stoppedSetup = (pull: number | null): ProjectSetup => ({
		...waiting,
		setup: {
			...(waiting.setup as NonNullable<ProjectSetup["setup"]>),
			state: "stopped",
			reason:
				"Failed twice in a row: The setup failed: it reached the most turns",
			pull_request_number: pull,
			pull_request_url:
				pull === null ? null : `https://github.com/Adamniels/fodr/pull/${pull}`,
			head_commit: null,
			report: null,
			review: null,
		},
	});

	test("a setup stopped before it had a pull request runs again on a comment from its step", async () => {
		const { runSetupAgain, loadSetup } = renderPage({
			setup: stoppedSetup(null),
		});

		const panel = await screen.findByRole("article", { name: "Setting up" });
		const run = within(panel).getByRole("region", { name: "The setup run" });
		expect(within(run).getByText("Stopped")).toBeTruthy();
		expect(
			within(run).getByText(
				"Failed twice in a row: The setup failed: it reached the most turns.",
			),
		).toBeTruthy();
		expect(within(run).queryByRole("button", { name: "Merge" })).toBeNull();
		fireEvent.click(within(run).getByRole("button", { name: RUN_SETUP_AGAIN }));
		const send = within(run).getByRole("button", { name: "Send" });
		expect((send as HTMLButtonElement).disabled).toBe(true);
		fireEvent.change(within(run).getByLabelText(COMMENT_RUNS_AGAIN), {
			target: { value: "Use Postgres 16." },
		});
		fireEvent.click(send);
		await waitFor(() =>
			expect(runSetupAgain).toHaveBeenCalledWith(ID, "Use Postgres 16."),
		);
		await waitFor(() => expect(loadSetup).toHaveBeenCalledTimes(2));
	});

	test("a refused comment from the step says why", async () => {
		const { runSetupAgain } = renderPage({ setup: stoppedSetup(null) });
		runSetupAgain.mockImplementation(() =>
			Promise.reject(
				new ApiError({
					status: 409,
					title: "This setup is not run again from here",
					detail:
						"It has a pull request; comment on it on GitHub to run it again.",
				}),
			),
		);

		const run = await screen.findByRole("region", { name: "The setup run" });
		fireEvent.click(within(run).getByRole("button", { name: RUN_SETUP_AGAIN }));
		fireEvent.change(within(run).getByLabelText(COMMENT_RUNS_AGAIN), {
			target: { value: "Again." },
		});
		fireEvent.click(within(run).getByRole("button", { name: "Send" }));
		expect((await within(run).findByRole("alert")).textContent).toBe(
			"This setup is not run again from here. It has a pull request; comment on it on GitHub to run it again.",
		);
	});

	test("a setup stopped with a pull request is run again by a comment on it", async () => {
		renderPage({ setup: stoppedSetup(9) });

		const run = await screen.findByRole("region", { name: "The setup run" });
		expect(within(run).getByText(COMMENT_ON_PULL)).toBeTruthy();
		expect(
			within(run).queryByRole("button", { name: RUN_SETUP_AGAIN }),
		).toBeNull();
	});

	test("a project setting up shows its steps in place of its features", async () => {
		const { loadCards, loadSetup } = renderPage({ setup: failed });

		const panel = await screen.findByRole("article", { name: "Setting up" });
		expect(within(panel).getByText(SETTING_UP)).toBeTruthy();
		// The steps in their order, each done or not.
		const steps = within(panel).getByRole("list", { name: "Setup steps" });
		expect(
			within(steps)
				.getAllByRole("listitem")
				.map((step) => step.textContent),
		).toEqual([
			"DoneA valid .devflow/ on the default branch",
			"DoneThe recipe on the default branch",
			"DoneThe settings accepted",
			"DoneA worker on the recipe",
			"To doThe check passed",
		]);
		// The failed check's output, as text.
		const check = within(panel).getByRole("region", {
			name: "The newest check",
		});
		expect(within(check).getByText("Failed")).toBeTruthy();
		expect(within(check).getByText(/exited with 1/)).toBeTruthy();
		expect(check.querySelector("pre")?.textContent).toBe(
			"npm test\nFAIL src/list.test.ts\nTests: 1 failed, 11 passed",
		);
		// Its features and cards are not shown, nor read.
		expect(loadSetup).toHaveBeenCalledWith(ID);
		expect(screen.queryByRole("article", { name: "Cards" })).toBeNull();
		expect(loadCards).not.toHaveBeenCalled();
		// The workers are, since starting one is a step.
		expect(
			await screen.findByRole("article", { name: "Workers" }),
		).toBeTruthy();
	});

	/** A project whose setup run proposed guided and a path, its settings
	 * step waiting. */
	const proposed: ProjectSetup = {
		...REGISTERED,
		state: "setting_up",
		steps: [
			{ step: "setup", done: true },
			{ step: "recipe", done: true },
			{ step: "settings", done: false },
			{ step: "worker", done: true },
			{ step: "check", done: true },
		],
		proposal: {
			preset: "guided",
			why: "Its migrations are worth a look before they merge.",
			paths: ["migrations/**"],
		},
		settings_waits: true,
	};

	test("the settings step is not offered before the setup's pull request merged", async () => {
		// The setup run proposed its settings and waits for the owner to merge
		// it: the step does not wait yet, so it offers nothing to accept, and
		// the proposal is shown with the run.
		renderPage({
			setup: {
				...waiting,
				steps: waiting.steps.map((step) =>
					step.step === "settings" ? { ...step, done: false } : step,
				),
				proposal: proposed.proposal,
			},
		});

		const run = await screen.findByRole("region", { name: "The setup run" });
		expect(within(run).getByText("What it proposes")).toBeTruthy();
		expect(screen.queryByRole("region", { name: "The settings" })).toBeNull();
		expect(screen.queryByRole("button", { name: "Accept" })).toBeNull();
	});

	test("the settings step shows the proposal with accept and change", async () => {
		const { saveSettings, loadSettings, loadSetup } = renderPage({
			setup: proposed,
		});
		saveSettings.mockImplementation((_id, saved) =>
			Promise.resolve({
				...saved,
				trusted_logins: [{ login: "octo-owner", mine: true }],
				preset: "guided",
			} as ProjectSettings),
		);

		const step = await screen.findByRole("region", { name: "The settings" });
		expect(within(step).getByText(SETTINGS_PROPOSED)).toBeTruthy();
		expect(within(step).getByText("Guided")).toBeTruthy();
		expect(
			within(step).getByText(
				"Its migrations are worth a look before they merge.",
			),
		).toBeTruthy();
		expect(
			within(
				within(step).getByRole("list", { name: "Paths that wait for you" }),
			).getByText("migrations/**"),
		).toBeTruthy();

		// Accept fills in the preset's settings and the paths over the settings
		// as saved, and saves them through the settings route; the Settings
		// panel and the steps are read again.
		fireEvent.click(within(step).getByRole("button", { name: "Accept" }));
		await waitFor(() => expect(saveSettings).toHaveBeenCalledTimes(1));
		const [id, saved] = saveSettings.mock.calls[0] as unknown as [
			string,
			Record<string, unknown>,
		];
		expect(id).toBe(ID);
		expect(saved).toMatchObject({
			questions: false,
			waits_for_owner: "sometimes",
			wait_when_marked: true,
			wait_when_paths: true,
			wait_when_decision: true,
			wait_paths: ["migrations/**"],
			hold_label: "devflow-hold",
			trusted_logins: ["octo-owner"],
		});
		expect(saved).not.toHaveProperty("preset");
		await waitFor(() => expect(loadSetup).toHaveBeenCalledTimes(2));
		await waitFor(() => expect(loadSettings).toHaveBeenCalledTimes(3));
	});

	test("the settings step without a proposal asks to save the panel once", async () => {
		renderPage({ setup: { ...proposed, proposal: null } });

		const step = await screen.findByRole("region", { name: "The settings" });
		expect(within(step).getByText(SETTINGS_SAVE_ONCE)).toBeTruthy();
		expect(within(step).queryByRole("button", { name: "Accept" })).toBeNull();
	});

	test("a proposal that is not saved says why", async () => {
		const { saveSettings } = renderPage({ setup: proposed });
		saveSettings.mockImplementation(() =>
			Promise.reject(
				new ApiError({
					status: 422,
					title: "The settings break a rule",
					detail: "wait_paths must not start with /",
				}),
			),
		);

		const step = await screen.findByRole("region", { name: "The settings" });
		fireEvent.click(within(step).getByRole("button", { name: "Accept" }));
		expect((await within(step).findByRole("alert")).textContent).toBe(
			`${NOT_ACCEPTED} The settings break a rule. wait_paths must not start with /`,
		);
	});

	test("a failed check is sent back with a comment", async () => {
		const { sendSetupBack, loadSetup } = renderPage({
			setup: { ...failed, can_send_back: true },
		});

		const check = await screen.findByRole("region", {
			name: "The newest check",
		});
		fireEvent.click(within(check).getByRole("button", { name: SEND_BACK }));
		const send = within(check).getByRole("button", { name: "Send" });
		expect((send as HTMLButtonElement).disabled).toBe(true);
		fireEvent.change(within(check).getByLabelText(SEND_BACK_LABEL), {
			target: { value: "Install Redis for the cache tests." },
		});
		fireEvent.click(send);
		await waitFor(() =>
			expect(sendSetupBack).toHaveBeenCalledWith(
				ID,
				"Install Redis for the cache tests.",
			),
		);
		await waitFor(() => expect(loadSetup).toHaveBeenCalledTimes(2));
	});

	test("a check that cannot be sent back offers no comment", async () => {
		renderPage({ setup: failed });

		const check = await screen.findByRole("region", {
			name: "The newest check",
		});
		expect(within(check).queryByRole("button", { name: SEND_BACK })).toBeNull();
	});

	test("a sent back setup says why its worker is to be started", async () => {
		renderPage({
			setup: {
				...failed,
				setup: {
					number: 2,
					branch: "devflow/setup-2",
					sent_back: true,
					state: "queued",
					reason:
						"You sent the failed check back; an agent writes .devflow/ again next",
					pull_request_number: null,
					pull_request_url: null,
					running: false,
					head_commit: null,
					report: null,
					review: null,
				},
			},
		});

		const run = await screen.findByRole("region", { name: "The setup run" });
		expect(within(run).getByText(SENT_BACK_RUN)).toBeTruthy();
		expect(within(run).queryByText(NO_DEVFLOW)).toBeNull();
	});

	test("Check again asks for another check and says so", async () => {
		const { checkAgain, loadSetup } = renderPage({ setup: failed });
		const panel = await screen.findByRole("article", { name: "Setting up" });

		fireEvent.click(within(panel).getByRole("button", { name: "Check again" }));

		await waitFor(() => expect(checkAgain).toHaveBeenCalledWith(ID));
		await waitFor(() => expect(loadSetup).toHaveBeenCalledTimes(2));
		expect(
			await within(panel).findByText(
				"Another check was asked for: the next worker on this version runs it.",
			),
		).toBeTruthy();
		expect(
			within(panel).queryByRole("button", { name: "Check again" }),
		).toBeNull();
	});

	test("a running check, and a check's pictures, are shown", async () => {
		renderPage({
			setup: {
				...failed,
				checking: true,
				check: {
					...failedCheck,
					passed: true,
					exit_code: 0,
					pictures: ["home-desktop", "home-phone"],
				},
			},
		});
		const panel = await screen.findByRole("article", { name: "Setting up" });

		expect(within(panel).getByText(CHECK_RUNNING)).toBeTruthy();
		const pictures = within(panel).getAllByRole("img");
		expect(pictures.map((picture) => picture.getAttribute("src"))).toEqual([
			`/devflow/api/projects/${ID}/setup/checks/0190a8f0-0000-7000-8000-00000000c4e1/pictures/home-desktop`,
			`/devflow/api/projects/${ID}/setup/checks/0190a8f0-0000-7000-8000-00000000c4e1/pictures/home-phone`,
		]);
		expect(
			within(panel).queryByRole("button", { name: "Check again" }),
		).toBeNull();
	});

	test("a registered project shows its cards, and a newer version's failed check above them", async () => {
		renderPage({ setup: { ...failed, state: "registered" } });

		const panel = await screen.findByRole("article", { name: "Setting up" });
		expect(
			within(panel).getByText(
				"The newest version of this project's setup failed its check, so its workers on that version get no work.",
			),
		).toBeTruthy();
		expect(await screen.findByRole("article", { name: "Cards" })).toBeTruthy();
	});

	test("a registered project with nothing failed shows no setup", async () => {
		renderPage();

		expect(await screen.findByRole("article", { name: "Cards" })).toBeTruthy();
		expect(screen.queryByRole("article", { name: "Setting up" })).toBeNull();
	});
});

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

	test("its heading shows the preset the saved settings match", async () => {
		const saved = {
			pickup_label: "devflow",
			hold_label: "devflow-hold",
			attention_label: "devflow-attention",
			review_label: "devflow-review",
			waits_for_owner: "always",
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
			preset: "only_i_merge" as const,
		};
		renderPage({
			loadSettings: () => Promise.resolve(saved),
			saveSettings: (_, settings) =>
				Promise.resolve({
					...saved,
					questions: settings.questions ?? true,
					waits_for_owner: settings.waits_for_owner ?? "",
					preset: "fully_automatic" as const,
				}),
		});
		const header = () => document.querySelector("header");
		await waitFor(() =>
			expect(header()?.textContent).toContain("Only I merge"),
		);

		// The form's choice is not the project's until it is saved.
		const settings = within(
			await screen.findByRole("article", { name: "Settings" }),
		);
		fireEvent.click(settings.getByRole("button", { name: "Fully automatic" }));
		expect(header()?.textContent).toContain("Only I merge");
		fireEvent.click(settings.getByRole("button", { name: "Save" }));
		await waitFor(() =>
			expect(header()?.textContent).toContain("Fully automatic"),
		);
		expect(header()?.textContent).not.toContain("Only I merge");
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
