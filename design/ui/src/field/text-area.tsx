import type { ComponentProps } from "react";
import { Field, type FieldProps } from "./field";
import styles from "./field.module.css";

export type TextAreaProps = Omit<ComponentProps<"textarea">, "children"> &
	FieldProps;

/** A native textarea with its label above it, and its help and error tied to
 * it: "Text field, textarea, select" in section 4 of design/DESIGN.md.
 * Everything a native textarea takes passes through to it, ref included. */
export function TextArea({
	label,
	help,
	error,
	id,
	className,
	"aria-describedby": describedBy,
	"aria-invalid": invalid,
	...rest
}: TextAreaProps) {
	return (
		<Field
			label={label}
			help={help}
			error={error}
			id={id}
			describedBy={describedBy}
			invalid={invalid}
			className={[styles.textarea, className].filter(Boolean).join(" ")}
		>
			{(control) => <textarea {...rest} {...control} />}
		</Field>
	);
}
