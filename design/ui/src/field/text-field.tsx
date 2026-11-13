import type { ComponentProps } from "react";
import { Field, type FieldProps } from "./field";
import styles from "./field.module.css";

export type TextFieldProps = Omit<ComponentProps<"input">, "children"> &
	FieldProps;

/** A native input with its label above it, and its help and error tied to it:
 * "Text field, textarea, select" in section 4 of design/DESIGN.md.
 * Everything a native input takes passes through to it, ref included. */
export function TextField({
	label,
	help,
	error,
	id,
	className,
	"aria-describedby": describedBy,
	"aria-invalid": invalid,
	...rest
}: TextFieldProps) {
	return (
		<Field
			label={label}
			help={help}
			error={error}
			id={id}
			describedBy={describedBy}
			invalid={invalid}
			className={[styles.input, className].filter(Boolean).join(" ")}
		>
			{(control) => <input {...rest} {...control} />}
		</Field>
	);
}
