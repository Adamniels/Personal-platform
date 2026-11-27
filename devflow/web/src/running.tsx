// The "Running now" tab: a panel for each run under way of the owner's projects, oldest first, with
// its card, what it is doing, how long and how many tokens so far, the agent's log as it happens,
// and "Stop now". The list comes from GET /devflow/api/running and then its stream, the log of each
// run from its own; every text a worker sent is shown as text, never as HTML. See "The pages" in
// devflow/README.md.
import {
	Badge,
	Button,
	Dialog,
	EmptyState,
	Status,
	useToast,
} from "@platform/ui";
import {
	type QueryClient,
	useQuery,
	useQueryClient,
} from "@tanstack/react-query";
import {
	type MouseEvent,
	type ReactNode,
	useEffect,
	useId,
	useLayoutEffect,
	useRef,
	useState,
} from "react";
import { describe } from "./actions";
import { navigate, plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import type { components } from "./api/schema";
import { AGAIN, CLOSED, dataOf, type EventStream } from "./events";
import styles from "./running.module.css";
import { cardPath, featurePath } from "./sections";

export type RunningList = GetAnswer<"/running">;
export type RunningRun = RunningList["runs"][number];

/** A line of a run's log, as its stream sends it. */
export type LogLine = components["schemas"]["RunLogLine"];

/** How the tab reads the list, stops a run and opens a stream, Dev flow's
 * API in the page and a test's own in a test. */
export type RunningApi = {
	loadRunning: () => Promise<RunningList>;
	stopRun: (projectId: string, runId: string) => Promise<void>;
	openEvents: (url: string) => EventStream;
};

export const RUNNING = ["devflow", "running"] as const;

/** The list's stream. */
export const RUNNING_EVENTS = "/devflow/api/running/events";

/** A run's log's stream. */
export function logEvents(projectId: string, runId: string): string {
	return `/devflow/api/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}/log/events`;
}

/** The most lines a panel keeps, the last ones. */
export const MOST_LINES = 200;

/** How often the list is read while the tab is not open, for its count. */
const EVERY = 60_000;

/** The list, read as the page loads, every minute while the page is
 * visible, and when the window regains focus; while the tab is open, its
 * stream keeps it current. */
export function useRunning(load: RunningApi["loadRunning"]) {
	return useQuery({
		queryKey: RUNNING,
		queryFn: load,
		refetchInterval: EVERY,
		refetchIntervalInBackground: false,
		refetchOnWindowFocus: true,
	});
}

export type RunningProps = RunningApi & {
	list: RunningList | undefined;
	failed: boolean;
	/** The time, in milliseconds since the epoch, a run's time is counted
	 * to. */
	now: () => number;
};

/** The tab: its stream open while it shows, and a panel per run. */
export function Running({
	list,
	failed,
	now,
	stopRun,
	openEvents,
}: RunningProps) {
	const queries = useQueryClient();
	useListStream(openEvents, queries);
	const at = useTicking(now);

	if (!list) {
		return failed ? (
			<EmptyState
				title="Running now could not be read"
				detail="Dev flow did not answer. The page asks again by itself."
			/>
		) : null;
	}

	if (list.runs.length === 0) {
		return (
			<EmptyState
				title="Nothing is running"
				detail="Your workers are waiting for work."
			/>
		);
	}

	return (
		<ul className={styles.panels} aria-label="Runs under way">
			{list.runs.map((run) => (
				<li key={run.run_id} className={styles.item}>
					<RunPanel
						run={run}
						at={at}
						stopRun={stopRun}
						openEvents={openEvents}
						stopped={() => queries.invalidateQueries({ queryKey: RUNNING })}
					/>
				</li>
			))}
		</ul>
	);
}

/** Keeps the list current from its stream while the tab shows: each running
 * event replaces it. A stream closed for good, a 401 among the reasons, has
 * the list read once through the client, which refreshes the session, and is
 * opened again a moment later. */
function useListStream(open: RunningApi["openEvents"], queries: QueryClient) {
	useEffect(() => {
		let stream: EventStream | null = null;
		let again: number | undefined;
		let gone = false;
		const connect = () => {
			const opened = open(RUNNING_EVENTS);
			stream = opened;
			opened.addEventListener("running", (event) => {
				queries.setQueryData(RUNNING, dataOf<RunningList>(event));
			});
			opened.addEventListener("error", () => {
				if (gone || opened.readyState !== CLOSED) return;
				void queries.invalidateQueries({ queryKey: RUNNING }).finally(() => {
					if (!gone) again = window.setTimeout(connect, AGAIN);
				});
			});
		};
		connect();
		return () => {
			gone = true;
			window.clearTimeout(again);
			stream?.close();
		};
	}, [open, queries]);
}

/** `now()`, read again each second, so a run's time ticks. */
function useTicking(now: () => number): number {
	const [at, setAt] = useState(now);
	useEffect(() => {
		const tick = window.setInterval(() => setAt(now()), 1000);
		return () => window.clearInterval(tick);
	}, [now]);
	return at;
}

/** What a run is about: its card's or its feature's number and title, or
 * what a setup's run and a check are. */
export function subjectOf(run: RunningRun): string {
	const number = run.card_number ?? run.feature_number;
	if (number !== null)
		return run.title ? `#${number} ${run.title}` : `#${number}`;
	return run.kind === "check" ? "Checking the recipe" : "Setting up";
}

/** The run's kind and state in words. */
export function stateOf(run: RunningRun): string {
	if (run.state === "checking") return "Checking";
	switch (run.kind) {
		case "build":
		case "setup":
			return "Building";
		case "fix":
		case "feature_fix":
			return run.fix_round === null
				? "Fixing"
				: `Fixing, round ${run.fix_round}`;
		case "review":
		case "feature_review":
		case "setup_review":
			return "Reviewing";
		case "conflict":
			return "Resolving a conflict";
		case "read":
			return "Reading";
		case "check":
			return "Checking";
		default:
			return "Running";
	}
}

/** How long, "14 min 5 s", from milliseconds. */
export function elapsed(ms: number): string {
	const total = Math.max(0, Math.floor(ms / 1000));
	const hours = Math.floor(total / 3600);
	const minutes = Math.floor((total % 3600) / 60);
	const seconds = total % 60;
	if (hours > 0) return `${hours} h ${minutes} min ${seconds} s`;
	if (minutes > 0) return `${minutes} min ${seconds} s`;
	return `${seconds} s`;
}

/** A count of tokens, short: "950", "1.5 k", "120 k", "2.1 M". */
export function amount(n: number): string {
	const one = (value: number) => value.toFixed(1).replace(/\.0$/, "");
	if (n < 1000) return String(n);
	if (n < 10_000) return `${one(n / 1000)} k`;
	// Below 999,500, so a count that rounds to 1000 k reads "1 M".
	if (n < 999_500) return `${Math.round(n / 1000)} k`;
	return `${one(n / 1_000_000)} M`;
}

type RunPanelProps = Pick<RunningApi, "stopRun" | "openEvents"> & {
	run: RunningRun;
	at: number;
	/** Called once a stop was asked for, so the list is read again. */
	stopped: () => unknown;
};

/** One run: who runs it and where, what it is doing, for how long and with
 * how many tokens, its log, and the way to its card and to stop it. */
function RunPanel({ run, at, stopRun, openEvents, stopped }: RunPanelProps) {
	const id = useId();
	const toast = useToast();
	const [asking, setAsking] = useState(false);
	const [asked, setAsked] = useState(false);
	const subject = subjectOf(run);
	const stopping = run.stopping || asked;
	const number = run.card_number ?? run.feature_number;
	const href =
		run.card_number !== null
			? cardPath(run.project_id, run.card_number)
			: run.feature_number !== null
				? featurePath(run.project_id, run.feature_number)
				: null;

	async function stop() {
		try {
			await stopRun(run.project_id, run.run_id);
			setAsked(true);
			setAsking(false);
			await stopped();
		} catch (error) {
			setAsking(false);
			toast.error(`Could not stop ${subject}: ${describe(error)}`);
		}
	}

	return (
		<article className={styles.panel} aria-labelledby={id}>
			<header className={styles.header}>
				<Badge>{run.project_name}</Badge>
				<h2 id={id} className={styles.title}>
					{number === null ? (
						subject
					) : (
						<>
							<span className={styles.number}>#{number}</span>
							{run.title ? ` ${run.title}` : null}
						</>
					)}
				</h2>
				<Status tone="running" className={styles.state}>
					{stopping ? "Stopping…" : stateOf(run)}
				</Status>
			</header>
			<p className={styles.meta}>
				<span>{run.worker_name}</span>
				{" · "}
				<span>{run.model ?? "no model"}</span>
				{" · "}
				<span>{elapsed(at - Date.parse(run.started_at))}</span>
			</p>
			<p className={styles.meta}>
				{`${amount(run.tokens)} tokens · ${amount(run.cache_reads)} cache reads`}
			</p>
			<RunLog
				key={run.run_id}
				projectId={run.project_id}
				runId={run.run_id}
				label={`Log of ${number === null ? subject.toLowerCase() : `#${number}`}`}
				openEvents={openEvents}
			/>
			<div className={styles.actions}>
				{href === null ? null : (
					<a
						className={styles.open}
						href={href}
						onClick={(event: MouseEvent<HTMLAnchorElement>) => {
							if (plainClick(event)) {
								event.preventDefault();
								navigate(href);
							}
						}}
					>
						Open card
					</a>
				)}
				{stopping ? null : (
					<Button kind="secondary" onClick={() => setAsking(true)}>
						Stop now
					</Button>
				)}
			</div>
			<Dialog
				open={asking}
				title="Stop this run?"
				confirmLabel="Stop now"
				onConfirm={stop}
				onCancel={() => setAsking(false)}
			>
				What it did is kept on its branch, and it waits for you.
			</Dialog>
		</article>
	);
}

type RunLogProps = Pick<RunningApi, "openEvents"> & {
	projectId: string;
	runId: string;
	label: string;
};

/** The run's log, the last lines as they arrive, kept scrolled to the end
 * unless the owner has scrolled up, with "Jump to latest" then. A line the
 * panel already has, after the stream opened again, is shown once. */
function RunLog({ projectId, runId, label, openEvents }: RunLogProps) {
	const [lines, setLines] = useState<LogLine[]>([]);
	const [following, setFollowing] = useState(true);
	const scroller = useRef<HTMLDivElement>(null);

	useEffect(() => {
		let stream: EventStream | null = null;
		let again: number | undefined;
		let gone = false;
		let ended = false;
		const connect = () => {
			const opened = openEvents(logEvents(projectId, runId));
			stream = opened;
			opened.addEventListener("line", (event) => {
				const line = dataOf<LogLine>(event);
				setLines((kept) => {
					const last = kept[kept.length - 1];
					if (last && line.seq <= last.seq) return kept;
					return [...kept, line].slice(-MOST_LINES);
				});
			});
			opened.addEventListener("end", () => {
				ended = true;
				opened.close();
			});
			opened.addEventListener("error", () => {
				if (gone || ended || opened.readyState !== CLOSED) return;
				again = window.setTimeout(connect, AGAIN);
			});
		};
		connect();
		return () => {
			gone = true;
			window.clearTimeout(again);
			stream?.close();
		};
	}, [openEvents, projectId, runId]);

	// Kept at the end as lines arrive, unless the owner has scrolled up.
	// biome-ignore lint/correctness/useExhaustiveDependencies: runs for each new line.
	useLayoutEffect(() => {
		const box = scroller.current;
		if (box && following) box.scrollTop = box.scrollHeight;
	}, [lines, following]);

	return (
		<div className={styles.logFrame}>
			<div
				ref={scroller}
				className={styles.log}
				role="log"
				aria-label={label}
				// Scrollable, so reachable from the keyboard.
				// biome-ignore lint/a11y/noNoninteractiveTabindex: a region that scrolls must take focus.
				tabIndex={0}
				onScroll={(event) => {
					const box = event.currentTarget;
					setFollowing(box.scrollHeight - box.scrollTop - box.clientHeight < 8);
				}}
			>
				{lines.length === 0 ? (
					<p className={styles.empty}>No line yet.</p>
				) : (
					<ol className={styles.lines}>
						{lines.map((line) => (
							<li key={line.seq} className={lineClass(line.kind)}>
								{lineText(line)}
							</li>
						))}
					</ol>
				)}
			</div>
			{following ? null : (
				<Button
					kind="secondary"
					className={styles.jump}
					onClick={() => {
						const box = scroller.current;
						if (box) box.scrollTop = box.scrollHeight;
						setFollowing(true);
					}}
				>
					Jump to latest
				</Button>
			)}
		</div>
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
