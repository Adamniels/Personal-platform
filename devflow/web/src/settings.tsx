// A project's settings page, /devflow/projects/<id>/settings, opened by "Edit settings" on its
// page and in its setup's settings step: in six sections, how much happens alone, the presets,
// whether a card's questions wait for the owner's answers, and whether a card's pull request waits
// for him, with what "sometimes" waits for; how cards are picked, the four labels and the trusted
// accounts whose cards, edits and comments count; the models and limits its runs get; its usage,
// the daily budget in millions of tokens; when it runs, always or by its schedules; and its
// values. A preset fills in the settings it covers in the form, and the side column names the
// preset the form matches, or Custom. All nineteen settings are saved together with one Save,
// never the preset, and a value Dev flow refuses is named under its field; when it runs, its
// schedules and the time zone, and its values are saved at once through their own routes. See
// "Settings" and "Schedules" in devflow/README.md.
import {
	Button,
	Card,
	Checkbox,
	Dialog,
	EmptyState,
	Select,
	Switch,
	TextArea,
	TextField,
} from "@platform/ui";
import {
	type FormEvent,
	type KeyboardEvent,
	useEffect,
	useId,
	useState,
} from "react";
import { plainClick } from "./address";
import { ApiError } from "./api/client";
import type { components } from "./api/schema";
import { USAGE_ANCHOR } from "./budget";
import type { ApiKeyStatus } from "./claude-login";
import type { Project } from "./projects";
import { type SchedulesApi, SchedulesCard } from "./schedules";
import { projectPath } from "./sections";
import styles from "./settings.module.css";
import { type ValuesApi, ValuesCard } from "./values";

export type ProjectSettings = components["schemas"]["ProjectSettings"];
/** What saving takes: the settings, the trusted accounts as plain logins. */
export type SettingsRequest = components["schemas"]["SettingsRequest"];

export const NOT_READ = "The settings could not be read. Try again.";
export const NOT_SAVED = "The settings were not saved. Try again.";
/** Why "The API key only" cannot be chosen yet. */
export const RUNS_ON_NO_KEY = "Give an API key on the Projects tab first";
export const SAVED = "Saved.";

/** How the panel reads and saves a project's settings, Dev flow's API in
 * the page and a test's own in a test. */
export type SettingsApi = {
	loadSettings: (id: string) => Promise<ProjectSettings>;
	/** Saves all eighteen and answers them as saved. */
	saveSettings: (
		id: string,
		settings: SettingsRequest,
	) => Promise<ProjectSettings>;
};

type Field = keyof ProjectSettings;

type LabelField =
	| "pickup_label"
	| "hold_label"
	| "attention_label"
	| "review_label";

type ConditionField =
	| "wait_when_marked"
	| "wait_when_paths"
	| "wait_when_decision";

/** The four labels, in the order they are shown, with what each does. */
const LABELS: { field: LabelField; label: string; help: string }[] = [
	{
		field: "pickup_label",
		label: "Pick-up label",
		help: "An open issue a trusted account wrote with this label becomes a card.",
	},
	{
		field: "hold_label",
		label: "Hold label",
		help: "A card with this label waits until it is taken off.",
	},
	{
		field: "attention_label",
		label: "Attention label",
		help: "What Dev flow marks a card that needs you with.",
	},
	{
		field: "review_label",
		label: "Review label",
		help: "A card, or a feature's parent, with this label is marked for your review, which sometimes waits for. Anyone may put it on; only a trusted account takes it off.",
	},
];

/** The three conditions of "sometimes", in the order the merge check reads
 * them. */
const CONDITIONS: { field: ConditionField; label: string }[] = [
	{
		field: "wait_when_marked",
		label: "The card or its feature is marked for your review",
	},
	{ field: "wait_when_paths", label: "The change touches a path on the list" },
	{ field: "wait_when_decision", label: "The agent made a decision for you" },
];

/** A preset's name, or custom when the settings match none. */
export type Preset = ProjectSettings["preset"];

/** The settings a preset covers. */
type Covered = Pick<
	ProjectSettings,
	"questions" | "waits_for_owner" | ConditionField
>;

/** The three presets, in the order they are offered, with what each fills
 * in. Guided ticks every condition; on never and always the conditions are
 * not read, so the others leave them as they are. */
const PRESETS: {
	value: Exclude<Preset, "custom">;
	name: string;
	fills: Partial<Covered>;
}[] = [
	{
		value: "fully_automatic",
		name: "Fully automatic",
		fills: { questions: false, waits_for_owner: "never" },
	},
	{
		value: "guided",
		name: "Guided",
		fills: {
			questions: false,
			waits_for_owner: "sometimes",
			wait_when_marked: true,
			wait_when_paths: true,
			wait_when_decision: true,
		},
	},
	{
		value: "only_i_merge",
		name: "Only I merge",
		fills: { questions: true, waits_for_owner: "always" },
	},
];

/** What each preset fills in, by its name on the wire: the setup's
 * settings step fills in a proposed preset the same way. */
export const PRESET_FILLS: Record<
	Exclude<Preset, "custom">,
	Partial<Covered>
> = Object.fromEntries(
	PRESETS.map((preset) => [preset.value, preset.fills]),
) as Record<Exclude<Preset, "custom">, Partial<Covered>>;

/** What saving takes, from the settings as read: everything but the preset,
 * which is never stored, the trusted accounts as plain logins. */
export const requestOf = ({
	preset: _,
	...settings
}: ProjectSettings): SettingsRequest => ({
	...settings,
	trusted_logins: settings.trusted_logins.map((entry) => entry.login),
});

/** Each preset's name as the panel and the page's heading show it. */
export const PRESET_NAMES: Record<Preset, string> = {
	fully_automatic: "Fully automatic",
	guided: "Guided",
	only_i_merge: "Only I merge",
	custom: "Custom",
};

/** The preset the settings match, or custom: the same rule as Dev flow's
 * own answer, Presets.Of, applied to the form as it is typed, before it is
 * saved. */
export const presetOf = (settings: Covered): Preset => {
	if (!settings.questions && settings.waits_for_owner === "never")
		return "fully_automatic";
	if (
		!settings.questions &&
		settings.waits_for_owner === "sometimes" &&
		settings.wait_when_marked &&
		settings.wait_when_paths &&
		settings.wait_when_decision
	)
		return "guided";
	if (settings.questions && settings.waits_for_owner === "always")
		return "only_i_merge";
	return "custom";
};

type ModelField = "model_reading" | "model_building" | "model_reviewing";

type LimitField =
	| "run_minutes"
	| "run_turns"
	| "run_budget_usd"
	| "fix_rounds_card"
	| "fix_rounds_feature";

/** The three models, in the order a card meets them, with which jobs each
 * runs. */
const MODELS: { field: ModelField; label: string; help: string }[] = [
	{
		field: "model_reading",
		label: "Reading model",
		help: "Reads each card before anything is built.",
	},
	{
		field: "model_building",
		label: "Building model",
		help: "A build, a fix, a conflict.",
	},
	{
		field: "model_reviewing",
		label: "Reviewing model",
		help: "Reviews a card, and a whole feature.",
	},
];

/** Claude Code's aliases, which follow the newest model of each family. */
const MODEL_NAMES: { value: string; name: string }[] = [
	{ value: "opus", name: "Opus" },
	{ value: "sonnet", name: "Sonnet" },
	{ value: "haiku", name: "Haiku" },
];

/** The five limits, each with the range Dev flow takes, which the field
 * offers too and its help says, or help of its own that says it. */
const LIMITS: {
	field: LimitField;
	label: string;
	min: number;
	max: number;
	help?: string;
}[] = [
	{
		field: "run_minutes",
		label: "Minutes a run may take",
		min: 10,
		max: 240,
	},
	{
		field: "run_turns",
		label: "Turns a run may take",
		min: 20,
		max: 1000,
	},
	{
		field: "run_budget_usd",
		label: "Spend per run, USD",
		min: 1,
		max: 100,
		help: "1 to 100. Claude Code ends a run that reaches it.",
	},
	{
		field: "fix_rounds_card",
		label: "Fix rounds a card gets",
		min: 0,
		max: 10,
	},
	{
		field: "fix_rounds_feature",
		label: "Fix rounds a feature gets",
		min: 0,
		max: 10,
	},
];

/** Why a limit's text is not sent: it is not a whole number. */
export const NOT_A_NUMBER = "must be a whole number";

/** The limits as their fields show them, as text, so a field emptied while
 * it is edited stays empty rather than becoming 0. */
type LimitTexts = Record<LimitField, string>;

const asTexts = (settings: ProjectSettings): LimitTexts => ({
	run_minutes: String(settings.run_minutes),
	run_turns: String(settings.run_turns),
	run_budget_usd: String(settings.run_budget_usd),
	fix_rounds_card: String(settings.fix_rounds_card),
	fix_rounds_feature: String(settings.fix_rounds_feature),
});

/** A limit's text as a whole number, or undefined when it is none. Whether
 * it is in range is Dev flow's to judge when it is saved. */
const asNumber = (text: string): number | undefined => {
	const trimmed = text.trim();
	return /^-?\d+$/.test(trimmed) ? Number(trimmed) : undefined;
};

/** The daily budget as the card shows it: whether it is on, and the millions
 * as their field shows them, as text, kept while it is off so switching it
 * back on shows them again. */
type BudgetForm = { on: boolean; millions: string };

/** A million tokens: the page takes and shows the budget in millions, and
 * Dev flow takes it in tokens. */
const MILLION = 1_000_000;

/** Why a budget Dev flow refused is named in the field's own unit, millions,
 * rather than in the tokens the route speaks. */
export const BUDGET_RANGE = "must be a whole number from 1 to 10 000";

/** What the field's help says counts, and what reaching it does. */
export const BUDGET_HELP =
	"Counts input, cache writes and output. When reached, nothing new starts until midnight.";

const asBudget = (budget: number | null): BudgetForm => ({
	on: budget !== null,
	millions: budget === null ? "" : String(budget / MILLION),
});

/** The list of paths as the box shows it, one pattern a line. */
const asLines = (paths: string[]) => paths.join("\n");

/** The box's lines as patterns: each trimmed, blank ones dropped. Whether
 * a pattern is valid is Dev flow's to judge when it is saved. */
const asPaths = (lines: string) =>
	lines
		.split("\n")
		.map((line) => line.trim())
		.filter((line) => line !== "");

export const NOT_DISCONNECTED = "The project was not disconnected. Try again.";
/** What the side column says of when a change counts. */
export const CHANGE_COUNTS =
	"A change counts from the next decision Dev flow makes. A run under way keeps the model and limits it started with.";

/** What each preset means, in one line, as the side column says it. */
export const PRESET_LINES: Record<Preset, string> = {
	fully_automatic:
		"Cards start on the agent's recommendations, and a pull request merges alone once its check and review pass.",
	guided:
		"Cards start on the agent's recommendations. A pull request waits for you when one of the three conditions holds; the rest merge alone.",
	only_i_merge:
		"A card with questions waits for your answers, and every pull request waits for you to merge it.",
	custom: "Your own mix of the settings below; it matches no preset.",
};

/** What the page reads and changes: the project, its settings and values,
 * and disconnecting it; Dev flow's API in the page and a test's own in a
 * test. */
export type SettingsPageApi = SettingsApi &
	ValuesApi &
	SchedulesApi & {
		loadProjects: () => Promise<Project[]>;
		/** Whether an API key is given, which "Runs on" offers only then. */
		loadApiKey: () => Promise<ApiKeyStatus>;
		disconnect: (id: string) => Promise<void>;
	};

export type SettingsPageProps = SettingsPageApi & {
	id: string;
	/** Goes to the project's page. */
	back: () => void;
	/** Goes to the Projects tab, once the project is disconnected. */
	disconnected: () => void;
};

/** The form as it is edited: the settings, with the paths, the limits and
 * the daily budget as their fields show them, as text. */
type Form = {
	values: ProjectSettings;
	lines: string;
	limits: LimitTexts;
	budget: BudgetForm;
};

const formOf = (settings: ProjectSettings): Form => ({
	values: settings,
	lines: asLines(settings.wait_paths),
	limits: asTexts(settings),
	budget: asBudget(settings.daily_token_budget),
});

/** The settings compared one by one, each counted once. */
const PLAIN: (
	| LabelField
	| ModelField
	| ConditionField
	| "questions"
	| "waits_for_owner"
	| "runs_on"
)[] = [
	"pickup_label",
	"hold_label",
	"attention_label",
	"review_label",
	"model_reading",
	"model_building",
	"model_reviewing",
	"questions",
	"waits_for_owner",
	"wait_when_marked",
	"wait_when_paths",
	"wait_when_decision",
	"runs_on",
];

/** How many of the twenty settings the form changes from the settings as
 * last read or saved. */
export const unsaved = (form: Form, base: ProjectSettings): number => {
	let count = PLAIN.filter(
		(field) => form.values[field] !== base[field],
	).length;
	const logins = (settings: ProjectSettings) =>
		settings.trusted_logins.map((entry) => entry.login).join("\n");
	if (logins(form.values) !== logins(base)) count += 1;
	if (asPaths(form.lines).join("\n") !== base.wait_paths.join("\n")) count += 1;
	for (const { field } of LIMITS)
		if (form.limits[field].trim() !== String(base[field])) count += 1;
	const was = asBudget(base.daily_token_budget);
	if (
		form.budget.on !== was.on ||
		(form.budget.on && form.budget.millions.trim() !== was.millions)
	)
		count += 1;
	return count;
};

/** The count as the Save bar says it. */
export const unsavedLine = (count: number): string =>
	count === 0
		? "No unsaved changes"
		: count === 1
			? "1 unsaved change"
			: `${count} unsaved changes`;

/** The id a section's card has on the page, by its name. */
const anchor = (name: string) => `settings-${name}`;

/** The ids of the six sections, which the side column links to; Usage's is
 * its own, which "Raise the budget" links to. */
const SECTION_LINKS: { id: string; label: string }[] = [
	{ id: anchor("alone"), label: "How much happens alone" },
	{ id: anchor("picked"), label: "How cards are picked" },
	{ id: anchor("runs"), label: "Runs" },
	{ id: USAGE_ANCHOR, label: "Usage" },
	{ id: anchor("schedules"), label: "When it runs" },
	{ id: anchor("values"), label: "Values" },
];

/** A project's settings page, /devflow/projects/<id>/settings: the header
 * with the way back to the project; the form, how much happens alone, how
 * cards are picked, runs and usage, with the cards "When it runs" and Values
 * and one Save bar under it;
 * and the side column with the preset the form matches, links to the six
 * sections and Disconnect. Nothing before the project is read. */
export function SettingsPage({
	id,
	back,
	disconnected,
	loadProjects,
	loadSettings,
	saveSettings,
	loadValues,
	giveValue,
	removeValue,
	loadSchedules,
	saveSchedules,
	saveTimeZone,
	loadApiKey,
	disconnect,
}: SettingsPageProps) {
	const [project, setProject] = useState<Project | null>();
	const [projectFailed, setProjectFailed] = useState(false);
	// The settings as last read or saved, which Undo goes back to.
	const [base, setBase] = useState<ProjectSettings>();
	const [form, setForm] = useState<Form>();
	const [readFailed, setReadFailed] = useState(false);
	const [saving, setSaving] = useState(false);
	const [saved, setSaved] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const [invalid, setInvalid] = useState<Partial<Record<Field, string>>>({});
	const [adding, setAdding] = useState("");
	const [leaving, setLeaving] = useState(false);
	const [leaveError, setLeaveError] = useState<string | null>(null);
	// Whether an API key is given; until it is read, the key is not offered.
	const [keyGiven, setKeyGiven] = useState(false);
	const formId = useId();
	const presetLabel = useId();
	// The card "Usage" is drawn only once the project and its settings are
	// read, after the browser looked for the address's #usage, so it is
	// scrolled to then.
	const drawn = Boolean(project) && base !== undefined;
	useEffect(() => {
		if (drawn && window.location.hash === `#${USAGE_ANCHOR}`)
			document.getElementById(USAGE_ANCHOR)?.scrollIntoView?.();
	}, [drawn]);

	useEffect(() => {
		let current = true;
		loadProjects().then(
			(projects) => {
				if (current) setProject(projects.find((p) => p.id === id) ?? null);
			},
			() => {
				if (current) setProjectFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadProjects]);

	useEffect(() => {
		let current = true;
		loadApiKey().then(
			(answer) => {
				if (current) setKeyGiven(answer.set);
			},
			() => {},
		);
		return () => {
			current = false;
		};
	}, [loadApiKey]);

	useEffect(() => {
		let current = true;
		loadSettings(id).then(
			(answer) => {
				if (!current) return;
				setBase(answer);
				setForm(formOf(answer));
			},
			() => {
				if (current) setReadFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadSettings]);

	/** Changes the form, and with it whether it was just saved. */
	const edit = (change: (before: Form) => Form) => {
		setForm((before) => (before ? change(before) : before));
		setSaved(false);
	};

	const set = <K extends keyof ProjectSettings>(
		field: K,
		value: ProjectSettings[K],
	) =>
		edit((before) => ({
			...before,
			values: { ...before.values, [field]: value },
		}));

	/** Fills in what the preset covers, and nothing else; saved only with
	 * Save, as any change. */
	const choose = (fills: Partial<Covered>) =>
		edit((before) => ({ ...before, values: { ...before.values, ...fills } }));

	/** Lists the login typed, unless it is empty or already listed whatever
	 * its case, as GitHub compares logins. Its shape is Dev flow's to judge
	 * when it is saved. */
	const add = () => {
		const login = adding.trim();
		setAdding("");
		if (login === "") return;
		edit((before) =>
			before.values.trusted_logins.some(
				(entry) => entry.login.toLowerCase() === login.toLowerCase(),
			)
				? before
				: {
						...before,
						values: {
							...before.values,
							trusted_logins: [
								...before.values.trusted_logins,
								{ login, mine: false },
							],
						},
					},
		);
	};

	/** Enter in the field adds the login rather than saving the form. */
	const addOnEnter = (event: KeyboardEvent<HTMLInputElement>) => {
		if (event.key !== "Enter") return;
		event.preventDefault();
		add();
	};

	const remove = (login: string) =>
		edit((before) => ({
			...before,
			values: {
				...before.values,
				trusted_logins: before.values.trusted_logins.filter(
					(entry) => entry.mine || entry.login !== login,
				),
			},
		}));

	/** Puts the form back to the settings as last read or saved. */
	const undo = () => {
		if (base === undefined) return;
		setForm(formOf(base));
		setInvalid({});
		setError(null);
		setSaved(false);
		setAdding("");
	};

	const save = async (event: FormEvent) => {
		event.preventDefault();
		if (form === undefined) return;
		// A limit that is not a whole number is named under its field, and
		// nothing is sent.
		const numbers: Partial<Record<LimitField, number>> = {};
		const wrong: (LimitField | "daily_token_budget")[] = [];
		for (const { field } of LIMITS) {
			const number = asNumber(form.limits[field]);
			if (number === undefined) wrong.push(field);
			else numbers[field] = number;
		}
		// The budget is sent in tokens, null while it is off.
		const millions = form.budget.on ? asNumber(form.budget.millions) : null;
		if (millions === undefined) wrong.push("daily_token_budget");
		if (wrong.length > 0) {
			setInvalid(
				Object.fromEntries(wrong.map((field) => [field, NOT_A_NUMBER])),
			);
			setError(null);
			setSaved(false);
			return;
		}
		setSaving(true);
		try {
			const answer = await saveSettings(id, {
				...requestOf(form.values),
				...(numbers as Record<LimitField, number>),
				wait_paths: asPaths(form.lines),
				daily_token_budget:
					millions === null || millions === undefined
						? null
						: millions * MILLION,
			});
			setBase(answer);
			setForm(formOf(answer));
			setInvalid({});
			setError(null);
			setSaved(true);
		} catch (refused) {
			const errors =
				refused instanceof ApiError && refused.problem.status === 422
					? refused.problem.errors
					: undefined;
			setInvalid(
				Object.fromEntries(
					Object.entries(errors ?? {}).map(([field, messages]) => [
						field,
						field === "daily_token_budget"
							? BUDGET_RANGE
							: (messages[0] ?? "is not valid"),
					]),
				),
			);
			setError(errors ? null : NOT_SAVED);
			setSaved(false);
		}
		setSaving(false);
	};

	const confirmDisconnect = async () => {
		setLeaving(false);
		try {
			await disconnect(id);
			setLeaveError(null);
			disconnected();
		} catch {
			setLeaveError(NOT_DISCONNECTED);
		}
	};

	if (projectFailed) {
		return (
			<p className={styles.error} role="alert">
				The project could not be read. Try again.
			</p>
		);
	}
	if (project === undefined) return null;
	if (project === null) {
		return (
			<EmptyState
				title="There is no such project"
				detail="It may have been disconnected."
				action={
					<a className={styles.back} href="/devflow/projects">
						Go to Projects
					</a>
				}
			/>
		);
	}

	const fullName = `${project.owner}/${project.name}`;
	const values = form?.values;
	const preset = values === undefined ? undefined : presetOf(values);
	const count =
		form === undefined || base === undefined ? 0 : unsaved(form, base);
	// Mine first, as it is always on.
	const trusted =
		values === undefined
			? []
			: [
					...values.trusted_logins.filter((entry) => entry.mine),
					...values.trusted_logins.filter((entry) => !entry.mine),
				];

	return (
		<>
			<header className={styles.header}>
				<a
					className={styles.back}
					href={projectPath(id)}
					onClick={(event) => {
						if (plainClick(event)) {
							event.preventDefault();
							back();
						}
					}}
				>
					<svg
						className={styles.chevron}
						viewBox="0 0 24 24"
						width="16"
						height="16"
						aria-hidden="true"
					>
						<path d="m15 18-6-6 6-6" />
					</svg>
					{project.name}
				</a>
				<p className={styles.caption}>{fullName}</p>
				<h2 className={styles.title}>Settings</h2>
				<p className={styles.lede}>
					How much Dev flow does on its own in {project.name}, which cards it
					takes, and how its runs are set.
				</p>
			</header>
			<div className={styles.columns}>
				<div className={styles.main}>
					{readFailed ? (
						<p className={styles.error} role="alert">
							{NOT_READ}
						</p>
					) : null}
					{error === null ? null : (
						<p className={styles.error} role="alert">
							{error}
						</p>
					)}
					{form === undefined || values === undefined ? null : (
						// The browser's own checks are off, so a value out of range is
						// named under its field as Dev flow judges it, as every other
						// field is.
						<form
							id={formId}
							aria-label="Settings"
							className={styles.sections}
							onSubmit={save}
							noValidate
						>
							<Card
								title="How much happens alone"
								id={anchor("alone")}
								className={styles.section}
							>
								<div className={styles.group}>
									<div className={styles.preset}>
										<span id={presetLabel} className={styles.label}>
											Preset
										</span>
										<div
											role="radiogroup"
											aria-labelledby={presetLabel}
											className={styles.track}
										>
											{PRESETS.map(({ value, name, fills }) => (
												<label key={value} className={styles.segment}>
													<input
														type="radio"
														name={`${formId}-preset`}
														value={value}
														className={styles.radio}
														checked={preset === value}
														onChange={() => choose(fills)}
													/>
													{name}
												</label>
											))}
										</div>
										<p className={styles.hint}>
											A preset fills in the settings below. Change one by hand
											and it shows Custom.
										</p>
									</div>
									<Switch
										className={styles.rule}
										label="Questions before starting"
										help="On: a card with questions waits for your answers under Waiting for you. Off: it starts on the agent's recommendations, listed on the card."
										checked={values.questions}
										onChange={(event) => set("questions", event.target.checked)}
									/>
									<div className={styles.waits}>
										<div className={styles.wait}>
											<Select
												label="Pull requests wait for you"
												help="Whether a card's pull request waits for you to merge it."
												value={values.waits_for_owner}
												error={invalid.waits_for_owner}
												onChange={(event) =>
													set("waits_for_owner", event.target.value)
												}
											>
												<option value="never">Never</option>
												<option value="sometimes">Sometimes</option>
												<option value="always">Always</option>
											</Select>
										</div>
										{values.waits_for_owner === "sometimes" ? (
											<fieldset className={styles.when}>
												<legend className={styles.label}>
													Waits for you when
												</legend>
												{CONDITIONS.map(({ field, label }) => (
													<Checkbox
														key={field}
														label={label}
														checked={values[field]}
														onChange={(event) =>
															set(field, event.target.checked)
														}
													/>
												))}
											</fieldset>
										) : null}
									</div>
									{values.waits_for_owner === "sometimes" ? (
										<TextArea
											label="Paths"
											help="One pattern a line, from the repository's top: * within a folder, ** across folders, and a folder ending in / for everything under it."
											autoComplete="off"
											spellCheck={false}
											rows={4}
											className={styles.mono}
											value={form.lines}
											error={invalid.wait_paths}
											onChange={(event) => {
												const lines = event.target.value;
												edit((before) => ({ ...before, lines }));
											}}
										/>
									) : null}
								</div>
							</Card>
							<Card
								title="How cards are picked"
								id={anchor("picked")}
								className={styles.section}
							>
								<div className={styles.group}>
									<div className={styles.pairs}>
										{LABELS.map(({ field, label, help }) => (
											<TextField
												key={field}
												label={label}
												help={help}
												autoComplete="off"
												spellCheck={false}
												maxLength={50}
												value={values[field]}
												error={invalid[field]}
												onChange={(event) => set(field, event.target.value)}
											/>
										))}
									</div>
									<div className={styles.rule}>
										<h3 className={styles.label}>Trusted accounts</h3>
										<p className={styles.hint}>
											Only the cards, edits, labels and comments of these GitHub
											accounts count; anyone else&apos;s are shown and never
											acted on. A login you add counts once you save.
										</p>
									</div>
									<ul
										className={styles.trustedList}
										aria-label="Trusted accounts"
									>
										{trusted.map((entry) => (
											<li key={entry.login} className={styles.trustedRow}>
												<span className={styles.trustedName}>
													{entry.login}
												</span>
												{entry.mine ? (
													<span className={styles.always}>you, always on</span>
												) : (
													<Button
														kind="ghost"
														aria-label={`Remove ${entry.login}`}
														onClick={() => remove(entry.login)}
													>
														Remove
													</Button>
												)}
											</li>
										))}
									</ul>
									<div className={styles.add}>
										<TextField
											label="Add a trusted account"
											placeholder="GitHub login"
											autoComplete="off"
											spellCheck={false}
											maxLength={39}
											value={adding}
											error={invalid.trusted_logins}
											onChange={(event) => setAdding(event.target.value)}
											onKeyDown={addOnEnter}
										/>
										<Button kind="secondary" onClick={add}>
											Add
										</Button>
									</div>
								</div>
							</Card>
							<Card title="Runs" id={anchor("runs")} className={styles.section}>
								<div className={styles.group}>
									<div className={styles.models}>
										{MODELS.map(({ field, label, help }) => (
											<Select
												key={field}
												label={label}
												help={help}
												value={values[field]}
												error={invalid[field]}
												onChange={(event) => set(field, event.target.value)}
											>
												{MODEL_NAMES.map(({ value, name }) => (
													<option key={value} value={value}>
														{name}
													</option>
												))}
											</Select>
										))}
									</div>
									<div className={`${styles.limits} ${styles.rule}`}>
										{LIMITS.map(({ field, label, min, max, help }) => (
											<TextField
												key={field}
												label={label}
												help={help ?? `${min} to ${max}`}
												className={styles.mono}
												type="number"
												inputMode="numeric"
												min={min}
												max={max}
												step={1}
												value={form.limits[field]}
												error={invalid[field]}
												onChange={(event) => {
													const text = event.target.value;
													edit((before) => ({
														...before,
														limits: { ...before.limits, [field]: text },
													}));
												}}
											/>
										))}
									</div>
								</div>
							</Card>
							<Card title="Usage" id={USAGE_ANCHOR} className={styles.section}>
								<div className={styles.group}>
									<Select
										label="Runs on"
										help={keyGiven ? undefined : RUNS_ON_NO_KEY}
										value={values.runs_on}
										error={invalid.runs_on}
										onChange={(event) => set("runs_on", event.target.value)}
									>
										<option value="subscription">Your subscription</option>
										<option value="api_key" disabled={!keyGiven}>
											The API key only
										</option>
									</Select>
									<Switch
										label="Daily budget"
										checked={form.budget.on}
										onChange={(event) => {
											const on = event.target.checked;
											edit((before) => ({
												...before,
												budget: { ...before.budget, on },
											}));
										}}
									/>
									{form.budget.on ? (
										<div className={styles.limits}>
											<TextField
												label="Million tokens a day"
												help={BUDGET_HELP}
												className={styles.mono}
												type="number"
												inputMode="numeric"
												min={1}
												max={10_000}
												step={1}
												value={form.budget.millions}
												error={invalid.daily_token_budget}
												onChange={(event) => {
													const text = event.target.value;
													edit((before) => ({
														...before,
														budget: { ...before.budget, millions: text },
													}));
												}}
											/>
										</div>
									) : null}
								</div>
							</Card>
						</form>
					)}
					<SchedulesCard
						id={id}
						anchor={anchor("schedules")}
						loadSchedules={loadSchedules}
						saveSchedules={saveSchedules}
						saveTimeZone={saveTimeZone}
					/>
					<ValuesCard
						id={id}
						anchor={anchor("values")}
						loadValues={loadValues}
						giveValue={giveValue}
						removeValue={removeValue}
					/>
					{form === undefined ? null : (
						<section className={styles.bar} aria-label="Save the settings">
							<output className={styles.count}>{unsavedLine(count)}</output>
							<div className={styles.barActions}>
								{saved ? (
									<output className={styles.saved}>{SAVED}</output>
								) : null}
								<Button kind="ghost" disabled={count === 0} onClick={undo}>
									Undo changes
								</Button>
								<Button
									kind="primary"
									type="submit"
									form={formId}
									disabled={saving}
								>
									Save
								</Button>
							</div>
						</section>
					)}
				</div>
				<div className={styles.side}>
					<Card title="These settings" className={styles.section}>
						{preset === undefined ? null : (
							<div className={styles.group}>
								<p className={styles.hero}>{PRESET_NAMES[preset]}</p>
								<p className={styles.hint}>{PRESET_LINES[preset]}</p>
								<p className={styles.hint}>{CHANGE_COUNTS}</p>
							</div>
						)}
					</Card>
					<nav className={styles.jump} aria-label="Sections">
						<ul className={styles.jumpList}>
							{SECTION_LINKS.map((link) => (
								<li key={link.id}>
									<a className={styles.jumpLink} href={`#${link.id}`}>
										{link.label}
									</a>
								</li>
							))}
						</ul>
					</nav>
					{leaveError === null ? null : (
						<p className={styles.error} role="alert">
							{leaveError}
						</p>
					)}
					<div className={styles.leave}>
						<Button
							kind="ghost"
							className={styles.danger}
							onClick={() => setLeaving(true)}
						>
							Disconnect {project.name}
						</Button>
					</div>
				</div>
			</div>
			<Dialog
				open={leaving}
				title={`Disconnect ${fullName}?`}
				confirmLabel="Disconnect"
				kind="destructive"
				onConfirm={confirmDisconnect}
				onCancel={() => setLeaving(false)}
			>
				Dev flow forgets this project and everything it read for it. Nothing
				changes on GitHub.
			</Dialog>
		</>
	);
}
