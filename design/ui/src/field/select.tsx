import type { ComponentProps } from "react";
import { Field, type FieldProps } from "./field";
import styles from "./field.module.css";

export type SelectProps = ComponentProps<"select"> & FieldProps;

/** A native select, with the native menu, its label above it, and its help
 * and error tied to it: "Text field, textarea, select" in section 4 of
 * design/DESIGN.md. Its options are `option` children, and everything a
 * native select takes passes through to it, ref included. */
export function Select({
	label,
	help,
	error,
	id,
	className,
	"aria-describedby": describedBy,
	"aria-invalid": invalid,
	...rest
}: SelectProps) {
	return (
		<Field
			label={label}
			help={help}
			error={error}
			id={id}
			describedBy={describedBy}
			invalid={invalid}
			className={[styles.select, className].filter(Boolean).join(" ")}
		>
			{(control) => <select {...rest} {...control} />}
		</Field>
	);
}
