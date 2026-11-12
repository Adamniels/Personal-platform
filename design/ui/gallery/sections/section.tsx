import { type ReactNode, useId } from "react";
import styles from "./section.module.css";

/** One component's section of the gallery: a card titled with the
 * component's name, which is also the region's accessible name. */
export function Section({
	title,
	children,
}: {
	title: string;
	children: ReactNode;
}) {
	const id = useId();
	return (
		<section aria-labelledby={id} className={styles.card}>
			<h2 id={id} className={styles.title}>
				{title}
			</h2>
			{children}
		</section>
	);
}

/** A labelled row of examples inside a section. */
export function Row({
	label,
	children,
}: {
	label: string;
	children: ReactNode;
}) {
	return (
		<div className={styles.row}>
			<p className={styles.label}>{label}</p>
			<div className={styles.items}>{children}</div>
		</div>
	);
}

/** A short line under the examples, for what a still page cannot show. */
export function Note({ children }: { children: ReactNode }) {
	return <p className={styles.note}>{children}</p>;
}
