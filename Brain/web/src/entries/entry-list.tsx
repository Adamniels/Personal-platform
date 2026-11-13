import type { Entry } from "./entries";
import styles from "./entries.module.css";

/** A list of entries, each its text, in the order given. */
export function EntryList({ entries }: { entries: readonly Entry[] }) {
	return (
		<ul className={styles.list}>
			{entries.map((entry) => (
				<li key={entry.id} className={styles.entry}>
					<p className={styles.text}>{entry.content}</p>
				</li>
			))}
		</ul>
	);
}
