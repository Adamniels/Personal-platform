import type { ComponentProps } from "react";
import styles from "./checkbox.module.css";

export type CheckboxProps = Omit<
	ComponentProps<"input">,
	"type" | "children"
> & {
	/** The text beside the box. The whole row is the label, so it is one target. */
	label: string;
	/** The completed to do look: the text in text-3 with a line through. */
	done?: boolean;
};

/** A native checkbox in a label row: "Checkbox" in section 4 of
 * design/DESIGN.md. Everything a native input takes passes through to the
 * box, ref included. */
export function Checkbox({
	label,
	done = false,
	className,
	...rest
}: CheckboxProps) {
	return (
		<label
			className={[styles.row, done && styles.done].filter(Boolean).join(" ")}
		>
			<input
				{...rest}
				type="checkbox"
				className={[styles.box, className].filter(Boolean).join(" ")}
			/>
			<span>{label}</span>
		</label>
	);
}
