// A project's page, /devflow/projects/<id>, opened from its row on the Projects tab: a header with
// the repository's name, the Settings panel under it, the Cards panel, and the Workers panel, with the project's worker key, the two commands
// that start a worker, and the workers that have said hello with it, each saying when it needs
// building again, and why no work is handed out when the user's Claude login is missing or past
// its renewal. The key is shown once, as the answer that made it, with the command that saves
// it, and never read back: after a reload the page knows only when it was made. See "The pages",
// "Workers" and "Starting a worker" in devflow/README.md.
import { Button, Card, Dialog, EmptyState, Status } from "@platform/ui";
import { useCallback, useEffect, useState } from "react";
import { plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import type { components } from "./api/schema";
import { type CardsApi, CardsPanel } from "./cards";
import styles from "./project.module.css";
import type { Project } from "./projects";
import { type SettingsApi, SettingsPanel } from "./settings";
import { duration } from "./waiting";

export type WorkerList = GetAnswer<"/projects/{id}/workers">;
export type Worker = WorkerList["workers"][number];
export type WorkerKey = components["schemas"]["WorkerKey"];

export const NO_WORKER = "No worker has said hello yet.";
export const NOT_READ = "The workers could not be read. Try again.";
export const NOT_MADE = "The key was not made. Try again.";
export const NOT_COPIED =
	"The key could not be copied. Select it and copy it by hand.";
export const SHOWN_ONCE =
	"Shown once. Copy it now: Dev flow keeps only a hash of it and cannot show it again.";
export const NOT_COPIED_COMMAND =
	"The command could not be copied. Select it and copy it by hand.";
export const PASTE =
	"Paste these in a terminal on the machine that runs the workers. Running the second again replaces the worker.";
/** What a worker that needs building again says, by why. */
export const rebuildNotice = (
	project: string,
	rebuild: NonNullable<Worker["rebuild"]>,
) =>
	rebuild === "recipe"
		? `${project}'s recipe has changed: run the command again`
		: "A new base image was released: run the command again";
/** Why no worker of the project is handed work, by the list's held. */
export const heldNotice = (held: string): string => {
	switch (held) {
		case "stopped":
			return "No work is handed out: something landed on a branch Dev flow watches, its default branch or a feature's, that Dev flow did not put there. Look at it under Waiting for you, and resume it there";
		case "unwatched":
			return "No work is handed out: Dev flow has not read its default branch in the last five minutes, and starts again once it has";
		case "claude_login_expired":
			return "No work is handed out: your Claude login is past its renewal. Give a new one on the Projects tab";
		default:
			return "No work is handed out until you give your Claude login on the Projects tab";
	}
};
/** After this long without a word from a worker, it shows as not seen
 * since then, as the slice settles. */
export const QUIET_MS = 10 * 60 * 1000;

/** How the page reads and changes a project's workers, Dev flow's API in
 * the page and a test's own in a test. */
export type ProjectApi = SettingsApi &
	CardsApi & {
		loadProjects: () => Promise<Project[]>;
		loadWorkers: (id: string) => Promise<WorkerList>;
		/** Makes the project's key, replacing its last, and answers it with
		 * the command that saves it. */
		makeKey: (id: string) => Promise<WorkerKey>;
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

/** The project's header, its Settings, Cards and Workers panels, or a not found state when the
 * caller has no project by that id. Nothing before the projects answer. */
export function ProjectPage({
	id,
	now,
	copy = clipboard,
	back,
	loadProjects,
	loadWorkers,
	makeKey,
	loadSettings,
	saveSettings,
	loadCards,
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
			<SettingsPanel
				id={id}
				loadSettings={loadSettings}
				saveSettings={saveSettings}
			/>
			<CardsPanel id={id} loadCards={loadCards} />
			<WorkersPanel
				id={id}
				project={project.name}
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
> & {
	/** The repository's name, as the rebuild notice names the project. */
	project: string;
	copy: (text: string) => Promise<void>;
};

/** The Workers panel: what the key is for, the key when it has just been
 * made, the commands that start a worker, the workers, and "Make a key", or
 * "Make a new key" behind a warning once there is one. */
function WorkersPanel({
	id,
	project,
	now,
	copy,
	loadWorkers,
	makeKey,
}: WorkersPanelProps) {
	const [list, setList] = useState<WorkerList>();
	const [readFailed, setReadFailed] = useState(false);
	const [key, setKey] = useState<WorkerKey | null>(null);
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
					{list?.held ? (
						<p className={styles.held}>
							<Status tone="needs-you">No work</Status>
							<span>{heldNotice(list.held)}</span>
						</p>
					) : null}
					{/* Six runs in a row failed outside their cards: a broken machine, a
					    usage limit or the network, said rather than left silent. See
					    "Stops" in devflow/README.md. */}
					{list?.cannot_run ? (
						<p className={styles.held} role="status">
							<Status tone="failed">Cannot run</Status>
							<span>{list.cannot_run}</span>
						</p>
					) : null}
					{error === null ? null : (
						<p className={styles.error} role="alert">
							{error}
						</p>
					)}
					{key !== null ? (
						<ShownKey value={key.key} copy={copy} />
					) : list?.key_made_at ? (
						<p className={styles.line}>
							Its key was made <When at={list.key_made_at} />.
						</p>
					) : list ? (
						<p className={styles.line}>It has no key yet.</p>
					) : null}
					{list === undefined ? null : (
						<section className={styles.start} aria-label="Start a worker">
							<p className={styles.line}>{PASTE}</p>
							{key !== null ? (
								<Command
									label="Save the key"
									copyLabel="Copy the save command"
									text={key.save_command}
									copy={copy}
								/>
							) : null}
							<Command
								label="Build and start the worker"
								copyLabel="Copy the start command"
								text={list.start_command}
								copy={copy}
							/>
						</section>
					)}
					{readFailed ? (
						<p className={styles.error} role="alert">
							{NOT_READ}
						</p>
					) : list === undefined ? null : list.workers.length === 0 ? (
						<p className={styles.muted}>{NO_WORKER}</p>
					) : (
						<ul className={styles.workers} aria-label="Workers">
							{list.workers.map((worker) => (
								<WorkerRow
									key={worker.id}
									worker={worker}
									project={project}
									now={now()}
								/>
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

/** A command to paste, labelled, in the mono font on an inset, with a
 * button that copies it; a feature's page shows its branch's with it. */
export function Command({
	label,
	copyLabel,
	text,
	copy,
}: {
	label: string;
	copyLabel: string;
	text: string;
	copy: (text: string) => Promise<void>;
}) {
	const [copied, setCopied] = useState(false);
	const [failed, setFailed] = useState(false);

	const copyCommand = async () => {
		try {
			await copy(text);
			setCopied(true);
			setFailed(false);
		} catch {
			setFailed(true);
		}
	};

	return (
		<section className={styles.command} aria-label={label}>
			<p className={styles.commandLabel}>{label}</p>
			<pre className={styles.commandText}>
				<code>{text}</code>
			</pre>
			{failed ? (
				<p className={styles.error} role="alert">
					{NOT_COPIED_COMMAND}
				</p>
			) : null}
			<div>
				<Button kind="secondary" onClick={copyCommand}>
					{copied ? "Copied" : copyLabel}
				</Button>
			</div>
		</section>
	);
}

/** A worker as an inset row: its name, what it runs, when it was last seen,
 * or, quiet for ten minutes, since when it has not been, and, when it needs
 * building again, why. */
function WorkerRow({
	worker,
	project,
	now,
}: {
	worker: Worker;
	project: string;
	now: number;
}) {
	const seen = Date.parse(worker.last_seen_at);
	const quiet = now - seen >= QUIET_MS;
	return (
		<li className={styles.worker}>
			<span className={styles.workerName}>{worker.name}</span>
			{worker.rebuild ? (
				<span className={styles.rebuild}>
					<Status tone="needs-you">Rebuild</Status>
					<span>{rebuildNotice(project, worker.rebuild)}</span>
				</span>
			) : null}
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
