import { type ReactNode, useId } from "react";
import styles from "./field.module.css";

/** What every field takes beside its native element's props. */
export type FieldProps = {
	/** Always shown, above the field, and the control's accessible name. */
	label: string;
	/** A line under the field, read as its description. */
	help?: string;
	/** What is wrong, without the prefix: the field adds "Error: " itself. */
	error?: string;
};

/** The attributes the field gives its control. */
type ControlProps = {
	id: string;
	"aria-describedby"?: string;
	"aria-invalid"?: boolean | "true" | "false" | "grammar" | "spelling";
	className: string;
};

/** "use at least 12 characters" becomes "Error: use at least 12 characters".
 * Text that already starts with the word keeps one copy of it. */
export function errorText(error: string): string {
	const what = error.replace(/^error\b[:\s]*/i, "");
	return what ? `Error: ${what}` : "Error";
}

/** The label, help and error around one control, shared by TextField,
 * TextArea and Select: "Text field, textarea, select" in section 4 of
 * design/DESIGN.md. The control is rendered by `children`, given the id,
 * description and validity that tie it to them. An id or aria-describedby
 * the caller passed is kept, the caller's description read after the field's
 * own. */
export function Field({
	label,
	help,
	error,
	id,
	describedBy,
	invalid,
	className,
	children,
}: FieldProps & {
	id: string | undefined;
	describedBy: string | undefined;
	invalid: ControlProps["aria-invalid"];
	className: string | undefined;
	children: (control: ControlProps) => ReactNode;
}) {
	const generated = useId();
	const controlId = id ?? generated;
	const helpId = `${controlId}-help`;
	const errorId = `${controlId}-error`;

	// The error is read before the help, since it is what needs acting on.
	const description =
		[error && errorId, help && helpId, describedBy].filter(Boolean).join(" ") ||
		undefined;

	return (
		<div className={styles.field}>
			<label htmlFor={controlId} className={styles.label}>
				{label}
			</label>
			{children({
				id: controlId,
				"aria-describedby": description,
				"aria-invalid": error ? "true" : invalid,
				className: [styles.control, className].filter(Boolean).join(" "),
			})}
			{error && (
				<p id={errorId} className={styles.error}>
					{errorText(error)}
				</p>
			)}
			{help && (
				<p id={helpId} className={styles.help}>
					{help}
				</p>
			)}
		</div>
	);
}
