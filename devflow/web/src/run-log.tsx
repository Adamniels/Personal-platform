// A run's log, as "Running now" shows it as it happens and a card's and a feature's pages show a
// finished run's: its lines in their order, each as text, never as HTML, a tool's name in bold. A
// finished run's log is read from GET /devflow/api/projects/{id}/runs/{run}/log, a page at a time,
// the next with "Show more", and is not live. See "A card's page" and "A feature's page" in
// devflow/README.md.
import { Button } from "@platform/ui";
import {
	type ReactNode,
	useCallback,
	useEffect,
	useRef,
	useState,
} from "react";
import type { components } from "./api/schema";
import styles from "./run-log.module.css";

/** A line of a run's log, as its stream sends it and its page answers it. */
export type LogLine = components["schemas"]["RunLogLine"];

/** A page of a finished run's log, and whether it has more. */
export type RunLogPage = components["schemas"]["RunLogPage"];

/** Reads the run's lines after the seq `after`, a page of them. */
export type LoadRunLog = (
	project: string,
	run: string,
	after: number,
) => Promise<RunLogPage>;

/** What a run whose log was pruned says in its place. */
export const KEPT_90_DAYS = "Its log was kept for 90 days.";

export const LOG_NOT_READ = "The log could not be read. Try again.";

/** The log's frame, box and its empty line, which "Running now" shares. */
export const logStyles = styles;

/** The lines, in the order given. */
export function LogLines({ lines }: { lines: LogLine[] }) {
	return (
		<ol className={styles.lines}>
			{lines.map((line) => (
				<li key={line.seq} className={lineClass(line.kind)}>
					{lineText(line)}
				</li>
			))}
		</ol>
	);
}

function lineClass(kind: string): string {
	switch (kind) {
		case "tool_result":
			return `${styles.line} ${styles.muted}`;
		case "check":
		case "system":
			return `${styles.line} ${styles.mono}`;
		default:
			return styles.line ?? "";
	}
}

/** A line's text as text; a tool's name, its first word, in bold. */
function lineText(line: LogLine): ReactNode {
	if (line.kind !== "tool") return line.text;
	const space = line.text.indexOf(" ");
	const name = space < 0 ? line.text : line.text.slice(0, space);
	return (
		<>
			<strong>{name}</strong>
			{space < 0 ? null : line.text.slice(space)}
		</>
	);
}

type FinishedLogProps = {
	project: string;
	run: string;
	/** The log region's name. */
	label: string;
	load: LoadRunLog;
};

/** A finished run's log, its first page read as it opens and each next one
 * with "Show more", added under the lines already shown; a line shown
 * already is shown once. */
export function FinishedLog({ project, run, label, load }: FinishedLogProps) {
	const [lines, setLines] = useState<LogLine[]>([]);
	const [more, setMore] = useState(false);
	const [state, setState] = useState<"reading" | "read" | "failed">("reading");
	const shown = useRef(true);
	useEffect(() => {
		shown.current = true;
		return () => {
			shown.current = false;
		};
	}, []);

	const read = useCallback(
		async (after: number) => {
			setState("reading");
			try {
				const page = await load(project, run, after);
				if (!shown.current) return;
				setLines((kept) => {
					const last = kept[kept.length - 1]?.seq ?? 0;
					return [...kept, ...page.lines.filter((line) => line.seq > last)];
				});
				setMore(page.more);
				setState("read");
			} catch {
				if (shown.current) setState("failed");
			}
		},
		[project, run, load],
	);

	useEffect(() => {
		void read(0);
	}, [read]);

	const last = lines[lines.length - 1]?.seq ?? 0;
	return (
		<div className={styles.logFrame}>
			<div
				className={`${styles.log} ${styles.finished}`}
				role="log"
				aria-label={label}
				aria-busy={state === "reading"}
				// Scrollable, so reachable from the keyboard.
				// biome-ignore lint/a11y/noNoninteractiveTabindex: a region that scrolls must take focus.
				tabIndex={0}
			>
				{lines.length > 0 ? (
					<LogLines lines={lines} />
				) : state === "failed" ? null : (
					<p className={styles.empty}>
						{state === "reading" ? "Reading the log…" : "No line."}
					</p>
				)}
			</div>
			{state === "failed" ? (
				<div className={styles.after}>
					<p className={styles.empty} role="alert">
						{LOG_NOT_READ}
					</p>
					<Button kind="secondary" onClick={() => void read(last)}>
						Try again
					</Button>
				</div>
			) : more ? (
				<div className={styles.after}>
					<Button
						kind="secondary"
						disabled={state === "reading"}
						onClick={() => void read(last)}
					>
						Show more
					</Button>
				</div>
			) : null}
		</div>
	);
}
