// Asking for another topic, for "Place under…" and "Same as…", and the question "Same as" asks
// before it acts. Same as is not destructive, since Separate undoes it, so its button is primary.
import { Button, Dialog, Select } from "@platform/ui";
import { type FormEvent, useEffect, useRef, useState } from "react";
import styles from "./topic-page.module.css";

export type Choice = { id: string; name: string };

/** A select of the other topics, with Save and Cancel. Its name is the
 * button that opened it, "Place under…", and its label says what the
 * choice is. */
export function ChooseTopic({
	name,
	label,
	choices,
	onChoose,
	onCancel,
}: {
	name: string;
	label: string;
	choices: readonly Choice[];
	/** Called with the topic chosen. */
	onChoose: (choice: Choice) => unknown;
	onCancel: () => void;
}) {
	const [chosen, setChosen] = useState("");
	const [error, setError] = useState<string | undefined>();
	const [saving, setSaving] = useState(false);
	const select = useRef<HTMLSelectElement>(null);

	useEffect(() => {
		select.current?.focus();
	}, []);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (saving) return;
		const choice = choices.find(({ id }) => id === chosen);
		if (!choice) {
			setError("Choose a topic");
			return;
		}
		setSaving(true);
		try {
			await onChoose(choice);
		} catch {
			// The failure has raised its toast, and the form stays open.
		} finally {
			setSaving(false);
		}
	}

	return (
		<form
			className={styles.form}
			aria-label={name}
			noValidate
			onSubmit={submit}
		>
			<Select
				ref={select}
				label={label}
				value={chosen}
				error={error}
				onChange={(event) => {
					setChosen(event.target.value);
					setError(undefined);
				}}
			>
				<option value="" disabled>
					{choices.length === 0 ? "No other topic yet" : "Choose a topic"}
				</option>
				{choices.map((choice) => (
					<option key={choice.id} value={choice.id}>
						{choice.name}
					</option>
				))}
			</Select>
			<div className={styles.actions}>
				<Button type="submit" disabled={saving}>
					Save
				</Button>
				<Button kind="ghost" onClick={onCancel}>
					Cancel
				</Button>
			</div>
		</form>
	);
}

/** Asks before treating one topic as another. */
export function SameAsDialog({
	topic,
	target,
	onConfirm,
	onCancel,
}: {
	topic: string;
	target: string;
	onConfirm: () => Promise<unknown>;
	onCancel: () => void;
}) {
	return (
		<Dialog
			open
			title="Treat these as one topic?"
			confirmLabel="Treat as one"
			onConfirm={() =>
				onConfirm()
					// A failure has raised its toast already.
					.catch(() => undefined)
			}
			onCancel={onCancel}
		>
			{topic} will be read as {target} from now on, and listed under it as
			another name. You can separate them again.
		</Dialog>
	);
}
