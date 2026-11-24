// One item of Connections on the Projects tab, GitHub or the Claude login: one line with an icon,
// the name over a mono line, a status label and the one action, and whatever must open below it,
// an error or the token field. See "The pages" in devflow/README.md.
import { useId } from "react";
import styles from "./connection.module.css";

export type ConnectionProps = {
	/** The item's name, its heading and its accessible name. */
	name: string;
	/** Its icon, drawn for the eye alone. */
	icon: React.ReactNode;
	/** The mono line under the name: whom it is linked as, or its dates. */
	line: React.ReactNode;
	/** Its status label, a dot and a word. */
	status: React.ReactElement;
	/** Its one action. */
	action: React.ReactElement;
	/** It needs the user: the accent border, with "Needs you" for a reader. */
	attention?: boolean;
	/** What opens below the line: an error, or a field. */
	children?: React.ReactNode;
};

/** A `--surface` item 64px high, of a wrapping row of them. */
export function Connection({
	name,
	icon,
	line,
	status,
	action,
	attention = false,
	children,
}: ConnectionProps) {
	const id = useId();
	return (
		<article
			aria-labelledby={id}
			className={`${styles.item} ${attention ? styles.attention : ""}`}
		>
			<div className={styles.row}>
				<span className={styles.icon}>{icon}</span>
				<div className={styles.text}>
					<h3 id={id} className={styles.name}>
						{name}
					</h3>
					<p className={styles.line}>{line}</p>
				</div>
				{attention ? <span className={styles.hidden}>Needs you</span> : null}
				<div className={styles.end}>
					{status}
					{action}
				</div>
			</div>
			{children}
		</article>
	);
}
