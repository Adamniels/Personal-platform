import { type ReactNode, useId } from "react";
import styles from "./section.module.css";

/** One component's section of the gallery: a card titled with the
 * component's name, which is also the region's accessible name. */
export function Section({
	title,
	wide = false,
	children,
}: {
	title: string;
	/** Spans the whole grid, for examples that need the width. */
	wide?: boolean;
	children: ReactNode;
}) {
	const id = useId();
	const classes = [styles.card, wide && styles.wide].filter(Boolean).join(" ");
	return (
		<section aria-labelledby={id} className={classes}>
			<h2 id={id} className={styles.title}>
				{title}
			</h2>
			{children}
		</section>
	);
}

/** A labelled row of examples inside a section. With `stack`, the examples
 * stack and each fills the row's width, for one that lays out its own width. */
export function Row({
	label,
	stack = false,
	children,
}: {
	label: string;
	stack?: boolean;
	children: ReactNode;
}) {
	const classes = [styles.items, stack && styles.stack]
		.filter(Boolean)
		.join(" ");
	return (
		<div className={styles.row}>
			<p className={styles.label}>{label}</p>
			<div className={classes}>{children}</div>
		</div>
	);
}

/** A short line under the examples, for what a still page cannot show. */
export function Note({ children }: { children: ReactNode }) {
	return <p className={styles.note}>{children}</p>;
}
