import {
	Children,
	type ComponentProps,
	createContext,
	isValidElement,
	type ReactNode,
	useContext,
} from "react";
import hidden from "../visually-hidden.module.css";
import styles from "./timeline.module.css";

/** Whether an entry is the timeline's last, which draws no line. */
const LastContext = createContext(false);

export type TimelineProps = ComponentProps<"ol"> & {
	/** The `TimelineEntry`s, newest or oldest first as the page orders them.
	 * Each is a direct child, so the last one is known. */
	children: ReactNode;
};

/** A timeline, from "Timeline entry" in section 4 of design/DESIGN.md: an
 * ordered list of entries joined by a line down the rail. Everything a
 * native ol takes passes through, so give it an `aria-label` where the page
 * has no heading for it. */
export function Timeline({ className, children, ...rest }: TimelineProps) {
	const entries = Children.toArray(children);
	return (
		<ol
			className={[styles.timeline, className].filter(Boolean).join(" ")}
			{...rest}
		>
			{entries.map((entry, index) => (
				<LastContext.Provider
					// toArray gives every element a key, so its own is kept.
					key={isValidElement(entry) && entry.key !== null ? entry.key : index}
					value={index === entries.length - 1}
				>
					{entry}
				</LastContext.Provider>
			))}
		</ol>
	);
}

export type TimelineEntryProps = Omit<ComponentProps<"li">, "children"> & {
	/** When it happened, already formatted, shown in the mono font. */
	time: string;
	/** The entry's one line. */
	children: ReactNode;
	/** An optional small line under it. */
	detail?: ReactNode;
	/** The entry needs the user: the filled accent dot, and "Needs you" for
	 * assistive technology, so the mark is not colour alone. */
	attention?: boolean;
};

/** One entry of a `Timeline`: the rail with its ring, and the time, the line
 * and the detail. The rail is drawn, so it is hidden from assistive
 * technology. */
export function TimelineEntry({
	time,
	detail,
	attention = false,
	className,
	children,
	...rest
}: TimelineEntryProps) {
	const last = useContext(LastContext);
	const classes = [styles.entry, attention && styles.attention, className]
		.filter(Boolean)
		.join(" ");
	return (
		<li className={classes} {...rest}>
			<span className={styles.rail} aria-hidden="true">
				<span className={styles.dot} />
				{last ? null : <span className={styles.line} />}
			</span>
			<div className={styles.content}>
				<span className={styles.time}>{time}</span>
				{attention ? <span className={hidden.hidden}>Needs you</span> : null}
				<p className={styles.body}>{children}</p>
				{detail === undefined ? null : (
					<p className={styles.detail}>{detail}</p>
				)}
			</div>
		</li>
	);
}
