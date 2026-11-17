// A project's Settings panel, under its page's header: the labels that pick and hold its cards and
// mark what needs the owner, and whether a card's pull request waits for him. All four are saved
// together, and a value Dev flow refuses is named under its field. See "Cards" in
// devflow/README.md.
import { Button, Card, Select, TextField } from "@platform/ui";
import { type FormEvent, useEffect, useState } from "react";
import { ApiError } from "./api/client";
import type { components } from "./api/schema";
import styles from "./settings.module.css";

export type ProjectSettings = components["schemas"]["ProjectSettings"];

export const NOT_READ = "The settings could not be read. Try again.";
export const NOT_SAVED = "The settings were not saved. Try again.";
export const SAVED = "Saved.";

/** How the panel reads and saves a project's settings, Dev flow's API in
 * the page and a test's own in a test. */
export type SettingsApi = {
	loadSettings: (id: string) => Promise<ProjectSettings>;
	/** Saves all four and answers them as saved. */
	saveSettings: (
		id: string,
		settings: ProjectSettings,
	) => Promise<ProjectSettings>;
};

type Field = keyof ProjectSettings;

/** The three labels, in the order they are shown, with what each does. */
const LABELS: { field: Field; label: string; help: string }[] = [
	{
		field: "pickup_label",
		label: "Pick-up label",
		help: "An open issue you wrote with this label becomes a card.",
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
];

/** The Settings panel: the four settings as a form, and Save. Nothing is
 * shown in it before they are read. */
export function SettingsPanel({
	id,
	loadSettings,
	saveSettings,
}: SettingsApi & { id: string }) {
	const [values, setValues] = useState<ProjectSettings>();
	const [readFailed, setReadFailed] = useState(false);
	const [saving, setSaving] = useState(false);
	const [saved, setSaved] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const [invalid, setInvalid] = useState<Partial<Record<Field, string>>>({});

	useEffect(() => {
		let current = true;
		loadSettings(id).then(
			(answer) => {
				if (current) setValues(answer);
			},
			() => {
				if (current) setReadFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadSettings]);

	const change = (field: Field, value: string) => {
		setValues((before) => (before ? { ...before, [field]: value } : before));
		setSaved(false);
	};

	const save = async (event: FormEvent) => {
		event.preventDefault();
		if (values === undefined) return;
		setSaving(true);
		try {
			setValues(await saveSettings(id, values));
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

	return (
		<Card title="Settings" className={styles.card}>
			{readFailed ? (
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			) : values === undefined ? null : (
				<form className={styles.form} onSubmit={save}>
					<p className={styles.line}>
						Dev flow picks up the open issues you wrote that carry the pick-up
						label, as cards.
					</p>
					{error === null ? null : (
						<p className={styles.error} role="alert">
							{error}
						</p>
					)}
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
							<option value="always">Always</option>
						</Select>
					</div>
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
