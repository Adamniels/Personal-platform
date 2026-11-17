// A project's page, /devflow/projects/<id>, opened from its row on the Projects tab: a header with
// the repository's name, and the Workers panel, with the project's worker key and the workers that
// have said hello with it. The key is shown once, as the answer that made it, and never read back:
// after a reload the page knows only when it was made. See "The pages" and "Workers" in
// devflow/README.md.
import { Button, Card, Dialog, EmptyState } from "@platform/ui";
import { useCallback, useEffect, useState } from "react";
import { plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import styles from "./project.module.css";
import type { Project } from "./projects";
import { duration } from "./waiting";

export type WorkerList = GetAnswer<"/projects/{id}/workers">;
export type Worker = WorkerList["workers"][number];

export const NO_WORKER = "No worker has said hello yet.";
export const NOT_READ = "The workers could not be read. Try again.";
export const NOT_MADE = "The key was not made. Try again.";
export const NOT_COPIED =
	"The key could not be copied. Select it and copy it by hand.";
export const SHOWN_ONCE =
	"Shown once. Copy it now: Dev flow keeps only a hash of it and cannot show it again.";
/** After this long without a word from a worker, it shows as not seen
 * since then, as the slice settles. */
export const QUIET_MS = 10 * 60 * 1000;

/** How the page reads and changes a project's workers, Dev flow's API in
 * the page and a test's own in a test. */
export type ProjectApi = {
	loadProjects: () => Promise<Project[]>;
	loadWorkers: (id: string) => Promise<WorkerList>;
	/** Makes the project's key, replacing its last, and answers it. */
	makeKey: (id: string) => Promise<string>;
};

export type ProjectPageProps = ProjectApi & {
	id: string;
	/** The time, in milliseconds since the epoch, workers are judged by. */
	now: () => number;
	/** Copies text to the clipboard, the browser's by default. */
	copy?: (text: string) => Promise<void>;
	/** Goes back to the Projects tab. */
	back: () => void;
};

const fullName = (project: Project) => `${project.owner}/${project.name}`;

const clipboard = (text: string) => navigator.clipboard.writeText(text);

/** The project's header and its Workers panel, or a not found state when the
 * caller has no project by that id. Nothing before the projects answer. */
export function ProjectPage({
	id,
	now,
	copy = clipboard,
	back,
	loadProjects,
	loadWorkers,
	makeKey,
}: ProjectPageProps) {
	const [project, setProject] = useState<Project | null>();
	const [failed, setFailed] = useState(false);

	useEffect(() => {
		let current = true;
		loadProjects().then(
			(projects) => {
				if (current) setProject(projects.find((p) => p.id === id) ?? null);
			},
			() => {
				if (current) setFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadProjects]);

	const backLink = (
		<a
			className={styles.back}
			href="/devflow/projects"
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					back();
				}
			}}
		>
			Back to Projects
		</a>
	);

	if (failed) {
		return (
			<p className={styles.error} role="alert">
				The project could not be read. Try again.
			</p>
		);
	}
	if (project === undefined) return null;
	if (project === null) {
		return (
			<EmptyState
				title="There is no such project"
				detail="It may have been disconnected."
				action={backLink}
			/>
		);
	}

	return (
		<>
			<header className={styles.header}>
				{backLink}
				<h2 className={styles.name}>{fullName(project)}</h2>
			</header>
			<WorkersPanel
				id={id}
				now={now}
				copy={copy}
				loadWorkers={loadWorkers}
				makeKey={makeKey}
			/>
		</>
	);
}

type WorkersPanelProps = Pick<
	ProjectPageProps,
	"id" | "now" | "loadWorkers" | "makeKey"
> & { copy: (text: string) => Promise<void> };

/** The Workers panel: what the key is for, the key when it has just been
 * made, the workers, and "Make a key", or "Make a new key" behind a warning
 * once there is one. */
function WorkersPanel({
	id,
	now,
	copy,
	loadWorkers,
	makeKey,
}: WorkersPanelProps) {
	const [list, setList] = useState<WorkerList>();
	const [readFailed, setReadFailed] = useState(false);
	const [key, setKey] = useState<string | null>(null);
	const [making, setMaking] = useState(false);
	const [confirming, setConfirming] = useState(false);
	const [error, setError] = useState<string | null>(null);

	const reload = useCallback(
		() =>
			loadWorkers(id).then(
				(answer) => {
					setList(answer);
					setReadFailed(false);
				},
				() => setReadFailed(true),
			),
		[id, loadWorkers],
	);

	useEffect(() => {
		let current = true;
		loadWorkers(id).then(
			(answer) => {
				if (current) setList(answer);
			},
			() => {
				if (current) setReadFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadWorkers]);

	const make = async () => {
		setConfirming(false);
		setMaking(true);
		try {
			setKey(await makeKey(id));
			setError(null);
			await reload();
		} catch {
			setError(NOT_MADE);
		}
		setMaking(false);
	};

	const hasKey = key !== null || (list?.key_made_at ?? null) !== null;
	const action = hasKey ? (
		<Button
			kind="secondary"
			disabled={making || list === undefined}
			onClick={() => setConfirming(true)}
		>
			Make a new key
		</Button>
	) : (
		<Button
			kind="primary"
			disabled={making || list === undefined}
			onClick={make}
		>
			Make a key
		</Button>
	);

	return (
		<>
			<Card title="Workers" className={styles.card} footer={action}>
				<div className={styles.body}>
					<p className={styles.line}>
						A worker holds this project's key, and asks Dev flow for work with
						it.
					</p>
					{error === null ? null : (
						<p className={styles.error} role="alert">
							{error}
						</p>
					)}
					{key !== null ? (
						<ShownKey value={key} copy={copy} />
					) : list?.key_made_at ? (
						<p className={styles.line}>
							Its key was made <When at={list.key_made_at} />.
						</p>
					) : list ? (
						<p className={styles.line}>It has no key yet.</p>
					) : null}
					{readFailed ? (
						<p className={styles.error} role="alert">
							{NOT_READ}
						</p>
					) : list === undefined ? null : list.workers.length === 0 ? (
						<p className={styles.muted}>{NO_WORKER}</p>
					) : (
						<ul className={styles.workers} aria-label="Workers">
							{list.workers.map((worker) => (
								<WorkerRow key={worker.id} worker={worker} now={now()} />
							))}
						</ul>
					)}
				</div>
			</Card>
			<Dialog
				open={confirming}
				title="Make a new key?"
				confirmLabel="Make a new key"
				kind="destructive"
				onConfirm={make}
				onCancel={() => setConfirming(false)}
			>
				The old key stops working at once. Every worker holding it is refused
				until it is given the new one.
			</Dialog>
		</>
	);
}

/** The key, the one time it is shown, with a button that copies it. */
function ShownKey({
	value,
	copy,
}: {
	value: string;
	copy: (text: string) => Promise<void>;
}) {
	const [copied, setCopied] = useState(false);
	const [failed, setFailed] = useState(false);

	const copyKey = async () => {
		try {
			await copy(value);
			setCopied(true);
			setFailed(false);
		} catch {
			setFailed(true);
		}
	};

	return (
		<section className={styles.key} aria-label="The new key">
			<code className={styles.value}>{value}</code>
			<p className={styles.once}>{SHOWN_ONCE}</p>
			{failed ? (
				<p className={styles.error} role="alert">
					{NOT_COPIED}
				</p>
			) : null}
			<div>
				<Button kind="secondary" onClick={copyKey}>
					{copied ? "Copied" : "Copy the key"}
				</Button>
			</div>
		</section>
	);
}

/** A worker as an inset row: its name, what it runs, and when it was last
 * seen, or, quiet for ten minutes, since when it has not been. */
function WorkerRow({ worker, now }: { worker: Worker; now: number }) {
	const seen = Date.parse(worker.last_seen_at);
	const quiet = now - seen >= QUIET_MS;
	return (
		<li className={styles.worker}>
			<span className={styles.workerName}>{worker.name}</span>
			<span className={styles.runs}>
				Recipe <span className={styles.mono}>{worker.recipe_version}</span> on{" "}
				<span className={styles.mono}>{worker.base_image}</span>
			</span>
			<span className={quiet ? styles.quiet : styles.seen}>
				{quiet ? (
					<>
						Not seen since <When at={worker.last_seen_at} />
					</>
				) : (
					<>
						Last seen{" "}
						<time className={styles.mono} dateTime={worker.last_seen_at}>
							{duration(Math.max(0, now - seen))} ago
						</time>
					</>
				)}
			</span>
		</li>
	);
}

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

/** A moment, as the date and the time in the reader's own zone, in the
 * mono font. */
function When({ at }: { at: string }) {
	return (
		<time className={styles.mono} dateTime={at}>
			{DATE_TIME.format(new Date(at))}
		</time>
	);
}
