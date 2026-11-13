import type { ComponentProps } from "react";
import styles from "./progress.module.css";

/** What the bar measures: progress and context used take the accent, budget
 * used takes info. */
export type ProgressKind = "progress" | "budget";

export type ProgressProps = Omit<
	ComponentProps<"div">,
	"children" | "role" | "aria-label"
> & {
	/** From 0 to 100. A value outside that is clamped, and one that is not a
	 * number is 0. */
	value: number;
	/** What the bar measures, for assistive technology, which hears it with
	 * the value: "<label>: <value>%". */
	label: string;
	kind?: ProgressKind;
};

/** The value a bar shows: clamped to 0 to 100, and 0 when it is not a
 * number. */
export function clampPercent(value: number): number {
	if (Number.isNaN(value)) return 0;
	return Math.min(100, Math.max(0, value));
}

/** A progress bar, from "Progress bar and meter" in section 4 of
 * design/DESIGN.md: a 6px track with a fill. It is an image named by its
 * label and its value, rounded to a whole percent, while the fill is drawn
 * at the exact value. */
export function Progress({
	value,
	label,
	kind = "progress",
	className,
	...rest
}: ProgressProps) {
	const percent = clampPercent(value);
	return (
		<div
			{...rest}
			role="img"
			aria-label={`${label}: ${Math.round(percent)}%`}
			className={[styles.track, className].filter(Boolean).join(" ")}
		>
			<div
				className={[styles.fill, styles[kind]].join(" ")}
				style={{ width: `${percent}%` }}
			/>
		</div>
	);
}
