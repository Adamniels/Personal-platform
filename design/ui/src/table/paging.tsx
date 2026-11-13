import type { ComponentProps } from "react";
import { Button } from "../button/button";
import styles from "./paging.module.css";

export type PagingDirection = "previous" | "next";

export type PagingProps = Omit<ComponentProps<"nav">, "children"> & {
	/** The navigation's accessible name, "Pages" unless a page has more
	 * than one. */
	label?: string;
	/** The first and last row shown, counted from 1, and how many there are. */
	from: number;
	to: number;
	total: number;
	hasPrevious: boolean;
	hasNext: boolean;
	/** Called with which button was pressed. The caller pages. */
	onPage: (direction: PagingDirection) => void;
};

/** The paging under a table: where it is, "Rows 1 to 7 of 214", and
 * Previous and Next, each disabled where it cannot go. */
export function Paging({
	label = "Pages",
	from,
	to,
	total,
	hasPrevious,
	hasNext,
	onPage,
	className,
	...rest
}: PagingProps) {
	return (
		<nav
			aria-label={label}
			{...rest}
			className={[styles.paging, className].filter(Boolean).join(" ")}
		>
			<p className={styles.where} aria-live="polite">
				{`Rows ${from} to ${to} of ${total}`}
			</p>
			<div className={styles.buttons}>
				<Button disabled={!hasPrevious} onClick={() => onPage("previous")}>
					Previous
				</Button>
				<Button disabled={!hasNext} onClick={() => onPage("next")}>
					Next
				</Button>
			</div>
		</nav>
	);
}
