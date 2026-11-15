import { X } from "lucide-react";
import type { ComponentProps, ReactNode } from "react";
import styles from "./filters.module.css";

export type FilterRowProps = Omit<ComponentProps<"div">, "children"> & {
	/** The search field, normally a `TextField`, which grows to fill the row. */
	search?: ReactNode;
	/** The result count, such as "7 of 214", shown in mono on the right and
	 * announced politely when it changes. */
	count?: ReactNode;
	/** The other filters: selects and `FilterPill`s, in their order. */
	children?: ReactNode;
};

/** The wrapping row of filters above a table, from "Table" in section 4 of
 * design/DESIGN.md. It lays out what the caller puts in it; the filtering is
 * the caller's. */
export function FilterRow({
	search,
	count,
	className,
	children,
	...rest
}: FilterRowProps) {
	return (
		<div
			{...rest}
			className={[styles.row, className].filter(Boolean).join(" ")}
		>
			{search !== undefined && <div className={styles.search}>{search}</div>}
			{children}
			{count !== undefined && (
				<p className={styles.count} aria-live="polite">
					{count}
				</p>
			)}
		</div>
	);
}

export type FilterPillProps = Omit<ComponentProps<"span">, "children"> & {
	/** The filter in words, such as "Updated: this week". */
	label: string;
	/** Called when the pill's remove button is pressed. */
	onRemove: () => void;
};

/** A removable filter pill: 32px high, fully round, filled with the third
 * surface, with a 26px round remove button inside it named for the filter. */
export function FilterPill({
	label,
	onRemove,
	className,
	...rest
}: FilterPillProps) {
	return (
		<span
			{...rest}
			className={[styles.pill, className].filter(Boolean).join(" ")}
		>
			<span>{label}</span>
			<button
				type="button"
				className={styles.remove}
				aria-label={`Remove filter ${label}`}
				onClick={onRemove}
			>
				<X aria-hidden="true" size={14} />
			</button>
		</span>
	);
}
