// A project's Settings panel, under its page's header, in three groups: how cards are picked, the
// four labels and the trusted accounts whose cards, edits and comments count; how much happens
// alone, the presets, whether a card's questions wait for the owner's answers, and whether a card's
// pull request waits for him, with what "sometimes" waits for; and the models and limits its runs
// get. A preset fills in the settings it covers in the form, and the panel names the preset the
// form matches, or Custom. All eighteen are saved together, never the preset, and a value Dev flow
// refuses is named under its field. See "Settings" in devflow/README.md.
import {
	Badge,
	Button,
	Card,
	Checkbox,
	Select,
	TextArea,
	TextField,
} from "@platform/ui";
import { type FormEvent, type KeyboardEvent, useEffect, useState } from "react";
import { ApiError } from "./api/client";
import type { components } from "./api/schema";
import styles from "./settings.module.css";

export type ProjectSettings = components["schemas"]["ProjectSettings"];
/** What saving takes: the settings, the trusted accounts as plain logins. */
export type SettingsRequest = components["schemas"]["SettingsRequest"];

export const NOT_READ = "The settings could not be read. Try again.";
export const NOT_SAVED = "The settings were not saved. Try again.";
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

type TickField = ConditionField | "questions";

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
		help: "Builds and fixes a card, resolves a conflict and fixes a whole feature.",
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

/** The four limits, each with the range Dev flow takes, which the field
 * offers too. */
const LIMITS: {
	field: LimitField;
	label: string;
	help: string;
	min: number;
	max: number;
}[] = [
	{
		field: "run_minutes",
		label: "Minutes a run may take",
		help: "From 10 to 240. A run still going then is ended.",
		min: 10,
		max: 240,
	},
	{
		field: "run_turns",
		label: "Turns a run may take",
		help: "From 20 to 1000.",
		min: 20,
		max: 1000,
	},
	{
		field: "fix_rounds_card",
		label: "Fix rounds a card gets",
		help: "From 0 to 10. A card is stopped when its review asks for changes after these.",
		min: 0,
		max: 10,
	},
	{
		field: "fix_rounds_feature",
		label: "Fix rounds a feature gets",
		help: "From 0 to 10. A feature waits for you when its review asks for changes after these.",
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
	fix_rounds_card: String(settings.fix_rounds_card),
	fix_rounds_feature: String(settings.fix_rounds_feature),
});

/** A limit's text as a whole number, or undefined when it is none. Whether
 * it is in range is Dev flow's to judge when it is saved. */
const asNumber = (text: string): number | undefined => {
	const trimmed = text.trim();
	return /^-?\d+$/.test(trimmed) ? Number(trimmed) : undefined;
};

/** The list of paths as the box shows it, one pattern a line. */
const asLines = (paths: string[]) => paths.join("\n");

/** The box's lines as patterns: each trimmed, blank ones dropped. Whether
 * a pattern is valid is Dev flow's to judge when it is saved. */
const asPaths = (lines: string) =>
	lines
		.split("\n")
		.map((line) => line.trim())
		.filter((line) => line !== "");

/** The Settings panel: the settings as a form, and Save. Nothing is shown in
 * it before they are read. */
export function SettingsPanel({
	id,
	loadSettings,
	saveSettings,
	onSettings,
}: SettingsApi & {
	id: string;
	/** The settings as read and as each save answers them, for the page's
	 * heading. */
	onSettings?: (settings: ProjectSettings) => void;
}) {
	const [values, setValues] = useState<ProjectSettings>();
	const [readFailed, setReadFailed] = useState(false);
	const [saving, setSaving] = useState(false);
	const [saved, setSaved] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const [invalid, setInvalid] = useState<Partial<Record<Field, string>>>({});
	const [adding, setAdding] = useState("");
	const [lines, setLines] = useState("");
	const [limits, setLimits] = useState<LimitTexts>();

	useEffect(() => {
		let current = true;
		loadSettings(id).then(
			(answer) => {
				if (!current) return;
				setValues(answer);
				onSettings?.(answer);
				setLines(asLines(answer.wait_paths));
				setLimits(asTexts(answer));
			},
			() => {
				if (current) setReadFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadSettings, onSettings]);

	const change = (
		field: LabelField | ModelField | "waits_for_owner",
		value: string,
	) => {
		setValues((before) => (before ? { ...before, [field]: value } : before));
		setSaved(false);
	};

	const type = (field: LimitField, text: string) => {
		setLimits((before) => (before ? { ...before, [field]: text } : before));
		setSaved(false);
	};

	const tick = (field: TickField, checked: boolean) => {
		setValues((before) => (before ? { ...before, [field]: checked } : before));
		setSaved(false);
	};

	/** Fills in what the preset covers, and nothing else; saved only with
	 * Save, as any change. */
	const choose = (fills: Partial<Covered>) => {
		setValues((before) => (before ? { ...before, ...fills } : before));
		setSaved(false);
	};

	/** Lists the login typed, unless it is empty or already listed whatever
	 * its case, as GitHub compares logins. Its shape is Dev flow's to judge
	 * when it is saved. */
	const add = () => {
		const login = adding.trim();
		setAdding("");
		if (login === "") return;
		setValues((before) =>
			before === undefined ||
			before.trusted_logins.some(
				(entry) => entry.login.toLowerCase() === login.toLowerCase(),
			)
				? before
				: {
						...before,
						trusted_logins: [...before.trusted_logins, { login, mine: false }],
					},
		);
		setSaved(false);
	};

	/** Enter in the field adds the login rather than saving the form. */
	const addOnEnter = (event: KeyboardEvent<HTMLInputElement>) => {
		if (event.key !== "Enter") return;
		event.preventDefault();
		add();
	};

	const remove = (login: string) => {
		setValues((before) =>
			before
				? {
						...before,
						trusted_logins: before.trusted_logins.filter(
							(entry) => entry.mine || entry.login !== login,
						),
					}
				: before,
		);
		setSaved(false);
	};

	const save = async (event: FormEvent) => {
		event.preventDefault();
		if (values === undefined || limits === undefined) return;
		// A limit that is not a whole number is named under its field, and
		// nothing is sent.
		const numbers: Partial<Record<LimitField, number>> = {};
		const wrong: LimitField[] = [];
		for (const { field } of LIMITS) {
			const number = asNumber(limits[field]);
			if (number === undefined) wrong.push(field);
			else numbers[field] = number;
		}
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
			const { preset: _, ...settings } = values;
			const answer = await saveSettings(id, {
				...settings,
				...(numbers as Record<LimitField, number>),
				wait_paths: asPaths(lines),
				trusted_logins: values.trusted_logins.map((entry) => entry.login),
			});
			setValues(answer);
			onSettings?.(answer);
			setLines(asLines(answer.wait_paths));
			setLimits(asTexts(answer));
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
						messages[0] ?? "is not valid",
					]),
				),
			);
			setError(errors ? null : NOT_SAVED);
			setSaved(false);
		}
		setSaving(false);
	};

	const preset = values === undefined ? undefined : presetOf(values);

	return (
		<Card title="Settings" className={styles.card}>
			{readFailed ? (
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			) : values === undefined ||
				limits === undefined ||
				preset === undefined ? null : (
				// The browser's own checks are off, so a value out of range is named
				// under its field as Dev flow judges it, as every other field is.
				<form className={styles.form} onSubmit={save} noValidate>
					{error === null ? null : (
						<p className={styles.error} role="alert">
							{error}
						</p>
					)}
					<section className={styles.group} aria-labelledby={`picked-${id}`}>
						<h3 id={`picked-${id}`} className={styles.heading}>
							How cards are picked
						</h3>
						<p className={styles.help}>
							Dev flow picks up the open issues your trusted accounts wrote that
							carry the pick-up label, as cards.
						</p>
						<div className={styles.fields}>
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
									onChange={(event) => change(field, event.target.value)}
								/>
							))}
						</div>
						<h4 id={`trusted-${id}`} className={styles.subheading}>
							Trusted accounts
						</h4>
						<p className={styles.help}>
							Only the cards, edits and comments of these GitHub accounts are
							instructions; anyone else's are shown and never acted on. A login
							you add counts once you save.
						</p>
						<ul className={styles.logins} aria-label="Trusted accounts">
							{values.trusted_logins.map((entry) => (
								<li key={entry.login} className={styles.login}>
									<span className={styles.name}>{entry.login}</span>
									{entry.mine ? (
										<Badge>You</Badge>
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
					</section>
					<section className={styles.group} aria-labelledby={`alone-${id}`}>
						<h3 id={`alone-${id}`} className={styles.heading}>
							How much happens alone
						</h3>
						<p className={styles.help}>
							A preset fills in the settings below; you save it as any change.
							Labels, trusted accounts, models and limits are outside every
							preset.
						</p>
						<div className={styles.presets}>
							<fieldset className={styles.choices}>
								<legend className={styles.legend}>Presets</legend>
								{PRESETS.map(({ value, name, fills }) => (
									<Button
										key={value}
										kind="secondary"
										aria-pressed={preset === value}
										onClick={() => choose(fills)}
									>
										{name}
									</Button>
								))}
							</fieldset>
							<p className={styles.matched}>
								<span id={`preset-${id}`}>Preset</span>{" "}
								<output aria-labelledby={`preset-${id}`}>
									{PRESET_NAMES[preset]}
								</output>
							</p>
						</div>
						<Checkbox
							label="Questions before starting"
							checked={values.questions}
							onChange={(event) => tick("questions", event.target.checked)}
						/>
						<p className={styles.help}>
							Every card is read before anything is built. With this on, a card
							the reading found questions in waits for your answers under
							Waiting for you; off, it is built on the reader's recommendations.
						</p>
						<div className={styles.fields}>
							<Select
								label="Pull requests wait for you"
								help="Whether a card's pull request waits for you to merge it."
								value={values.waits_for_owner}
								error={invalid.waits_for_owner}
								onChange={(event) =>
									change("waits_for_owner", event.target.value)
								}
							>
								<option value="never">Never</option>
								<option value="sometimes">Sometimes</option>
								<option value="always">Always</option>
							</Select>
						</div>
						{values.waits_for_owner === "sometimes" ? (
							<fieldset className={styles.sometimes}>
								<legend className={styles.legend}>Waits for you when</legend>
								<p className={styles.help}>
									A card's pull request waits for you when any of these holds,
									and merges by itself otherwise.
								</p>
								<div className={styles.conditions}>
									{CONDITIONS.map(({ field, label }) => (
										<Checkbox
											key={field}
											label={label}
											checked={values[field]}
											onChange={(event) => tick(field, event.target.checked)}
										/>
									))}
								</div>
								<TextArea
									label="Paths"
									help="One pattern a line, from the repository's top: * within a folder, ** across folders, and a folder ending in / for everything under it."
									autoComplete="off"
									spellCheck={false}
									rows={4}
									className={styles.paths}
									value={lines}
									error={invalid.wait_paths}
									onChange={(event) => {
										setLines(event.target.value);
										setSaved(false);
									}}
								/>
							</fieldset>
						) : null}
					</section>
					<section className={styles.runs} aria-labelledby={`runs-${id}`}>
						<h3 id={`runs-${id}`} className={styles.heading}>
							Models and limits
						</h3>
						<p className={styles.help}>
							A change counts from the next job; a run under way keeps the model
							and limits it started with.
						</p>
						<div className={styles.fields}>
							{MODELS.map(({ field, label, help }) => (
								<Select
									key={field}
									label={label}
									help={help}
									value={values[field]}
									error={invalid[field]}
									onChange={(event) => change(field, event.target.value)}
								>
									{MODEL_NAMES.map(({ value, name }) => (
										<option key={value} value={value}>
											{name}
										</option>
									))}
								</Select>
							))}
						</div>
						<div className={styles.fields}>
							{LIMITS.map(({ field, label, help, min, max }) => (
								<TextField
									key={field}
									label={label}
									help={help}
									type="number"
									inputMode="numeric"
									min={min}
									max={max}
									step={1}
									value={limits[field]}
									error={invalid[field]}
									onChange={(event) => type(field, event.target.value)}
								/>
							))}
						</div>
					</section>
					<div className={styles.actions}>
						<Button kind="secondary" type="submit" disabled={saving}>
							Save
						</Button>
						{saved ? <output className={styles.saved}>{SAVED}</output> : null}
					</div>
				</form>
			)}
		</Card>
	);
}
