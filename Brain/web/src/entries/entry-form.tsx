import { Button, TextArea } from "@platform/ui";
import {
	type FormEvent,
	type ReactNode,
	useEffect,
	useRef,
	useState,
} from "react";
import { isFieldError } from "../providers";
import styles from "./entries.module.css";

export type EntryFormProps = {
	/** The text area's label: "New entry", "Entry", "New rule". */
	label: string;
	/** The form's accessible name, where it stands alone rather than in place
	 * of an entry. */
	name?: string;
	/** What the text area starts with, an entry's text when correcting it. */
	initial?: string;
	/** What is said when Save is pressed with no text. */
	emptyError: string;
	/** Fields above the text area, such as the category. */
	children?: ReactNode;
	/** Fields under it, such as its subjects. */
	after?: ReactNode;
	/** Whether the fields above the text area are right; each shows its own
	 * error. The form sends nothing until it and they are. */
	validate?: () => boolean;
	/** Whether the text area takes focus when the form opens. */
	focusText?: boolean;
	/** Saves the text, trimmed and never empty. A 422 it throws is shown on
	 * the text area; any other failure has its toast already. */
	onSave: (content: string) => Promise<unknown>;
	onCancel: () => void;
};

/** A text area with Save and Cancel under it, opened in place, to add an
 * entry or correct one. */
export function EntryForm({
	label,
	name,
	initial = "",
	emptyError,
	children,
	after,
	validate,
	focusText = true,
	onSave,
	onCancel,
}: EntryFormProps) {
	const [text, setText] = useState(initial);
	const [error, setError] = useState<string | undefined>();
	const [saving, setSaving] = useState(false);
	const field = useRef<HTMLTextAreaElement>(null);

	useEffect(() => {
		if (focusText) field.current?.focus();
	}, [focusText]);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (saving) return;
		const content = text.trim();
		const fieldsRight = validate?.() ?? true;
		setError(content === "" ? emptyError : undefined);
		if (content === "" || !fieldsRight) return;
		setSaving(true);
		try {
			await onSave(content);
		} catch (failure) {
			if (isFieldError(failure)) {
				setError(failure.problem.detail ?? failure.problem.title);
			}
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
			{children}
			<TextArea
				ref={field}
				label={label}
				rows={3}
				value={text}
				error={error}
				onChange={(event) => setText(event.target.value)}
			/>
			{after}
			<div className={styles.formActions}>
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
