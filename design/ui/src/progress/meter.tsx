import type { ComponentProps } from "react";
import { clampPercent, Progress, type ProgressKind } from "./progress";
import styles from "./progress.module.css";

export type MeterProps = Omit<ComponentProps<"div">, "children"> & {
	/** The short mono label on the left, such as "CTX". */
	label: string;
	/** From 0 to 100, clamped as the bar clamps it. */
	value: number;
	/** What the meter measures in words, such as "Context used", which is
	 * what assistive technology hears. Without it, the short label is used. */
	description?: string;
	kind?: ProgressKind;
};

/** A meter row, from "Progress bar and meter" in section 4 of
 * design/DESIGN.md: a mono label 28px wide, a progress bar and a mono value.
 * The bar says the label and the value, so the two texts beside it are
 * hidden from assistive technology rather than read twice. */
export function Meter({
	label,
	value,
	description,
	kind = "progress",
	className,
	...rest
}: MeterProps) {
	const percent = Math.round(clampPercent(value));
	return (
		<div
			{...rest}
			className={[styles.meter, className].filter(Boolean).join(" ")}
		>
			<span
				className={[styles.mono, styles.label].join(" ")}
				aria-hidden="true"
			>
				{label}
			</span>
			<Progress
				value={value}
				label={description ?? label}
				kind={kind}
				className={styles.bar}
			/>
			<span
				className={[styles.mono, styles.value].join(" ")}
				aria-hidden="true"
			>
				{percent}%
			</span>
		</div>
	);
}
