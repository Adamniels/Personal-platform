import { type ComponentProps, useId } from "react";
import styles from "./switch.module.css";

export type SwitchProps = Omit<
	ComponentProps<"input">,
	"type" | "role" | "children"
> & {
	/** The setting the switch turns on and off, its accessible name. The
	 * whole row is the label, so it is one target. */
	label: string;
	/** One line under the label, such as what on and off mean, which
	 * describes the switch. */
	help?: string;
};

/** A switch in a label row: "Switch" in section 4 of design/DESIGN.md. A
 * native checkbox with role switch, so a screen reader says on and off, drawn
 * as the track with the thumb over it. Everything a native input takes passes
 * through to it, ref included; `className` goes to the row. */
export function Switch({
	label,
	help,
	className,
	"aria-describedby": describedBy,
	...rest
}: SwitchProps) {
	const labelId = useId();
	const helpId = useId();
	const described =
		[describedBy, help === undefined ? undefined : helpId]
			.filter(Boolean)
			.join(" ") || undefined;
	return (
		<label className={[styles.row, className].filter(Boolean).join(" ")}>
			<span className={styles.text}>
				{/* Named by its label alone, not by the help beside it in the row. */}
				<span id={labelId} className={styles.label}>
					{label}
				</span>
				{help === undefined ? null : (
					<span id={helpId} className={styles.help}>
						{help}
					</span>
				)}
			</span>
			<span className={styles.control}>
				<input
					{...rest}
					type="checkbox"
					// biome-ignore lint/a11y/useAriaPropsForRole: a native checkbox says its state through checked, and ARIA in HTML forbids aria-checked where checked applies; a copy would go stale on an uncontrolled switch.
					role="switch"
					aria-labelledby={labelId}
					aria-describedby={described}
					className={styles.track}
				/>
				<span className={styles.thumb} aria-hidden="true" />
			</span>
		</label>
	);
}
