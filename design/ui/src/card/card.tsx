import { type ComponentProps, type ReactNode, useId } from "react";
import hidden from "../visually-hidden.module.css";
import styles from "./card.module.css";

export type CardProps = Omit<ComponentProps<"article">, "title"> & {
	/** The card's label, its h2 and its accessible name. */
	title: string;
	/** A caption or a status label, on the right of the title. */
	aside?: ReactNode;
	/** The card's one action, pushed to the bottom. */
	footer?: ReactNode;
	/** The card needs the user: the amber outline, and "Needs you" for
	 * assistive technology, so the mark is not colour alone. */
	attention?: boolean;
	/** The body. The design's one key fact and at most three rows are for the
	 * page to keep to; the card does not count them. */
	children?: ReactNode;
};

/** "Card" in section 4 of design/DESIGN.md: an article with a header, a body
 * and an optional footer. Everything a native article takes passes through.
 * Laid out in a card grid, cards in a row stretch to one height, so their
 * footers align. */
export function Card({
	title,
	aside,
	footer,
	attention = false,
	className,
	children,
	...rest
}: CardProps) {
	const id = useId();
	const classes = [styles.card, attention && styles.attention, className]
		.filter(Boolean)
		.join(" ");
	return (
		<article aria-labelledby={id} className={classes} {...rest}>
			<div className={styles.header}>
				<h2 id={id} className={styles.title}>
					{title}
				</h2>
				{attention ? <span className={hidden.hidden}>Needs you</span> : null}
				{aside === undefined ? null : (
					<div className={styles.aside}>{aside}</div>
				)}
			</div>
			{children === undefined ? null : <div>{children}</div>}
			{footer === undefined ? null : (
				<div className={styles.footer}>{footer}</div>
			)}
		</article>
	);
}
