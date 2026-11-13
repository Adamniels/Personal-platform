import type { ComponentProps } from "react";
import styles from "./status.module.css";

/** The five statuses of "Badge and status label" in section 4 of
 * design/DESIGN.md. There are exactly five. */
export type StatusTone = "running" | "needs-you" | "done" | "failed" | "idle";

export type StatusProps = Omit<ComponentProps<"span">, "children"> & {
	/** Decides the colours. */
	tone: StatusTone;
	/** The word, the caller's own, since a feature has its own words. Required,
	 * since a status is always a dot plus a word (hard rule 5). */
	children: string;
};

/** A status label: a dot in the text colour plus a word, on the tone's soft
 * fill. The dot is decoration, so the word alone is what is read. Everything
 * a native span takes passes through. */
export function Status({ tone, className, children, ...rest }: StatusProps) {
	const classes = [styles.label, styles.status, styles[tone], className]
		.filter(Boolean)
		.join(" ");
	return (
		<span className={classes} {...rest}>
			<span className={styles.dot} aria-hidden="true" />
			{children}
		</span>
	);
}
