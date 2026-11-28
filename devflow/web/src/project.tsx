// A project's page, /devflow/projects/<id>, opened from its row on the Projects tab, cards first:
// a header with the way back, a mono caption with the repository, when it was read and the preset
// its saved settings match, the name as the page title, a summary line and the stat pills; then
// the cards column, its features and cards on their own, or while the project is setting up its
// Setup panel in their place; and beside it the side column, the Workers card, the card "When it
// runs", with its windows' switches and when it runs next, and the settings summary, whose "Edit
// settings" opens the project's settings page. The Workers card shows
// the workers that have said hello with the project's key, each saying when it needs building
// again, why no work is handed out when none is, and, behind "Start a worker", the two commands
// that start one. The key is shown once, as the answer that made it, with the command that saves
// it, and never read back: after a reload the page knows only when it was made. See "The pages",
// "Workers" and "Starting a worker" in devflow/README.md.
import { Badge, Button, Card, Dialog, EmptyState, Status } from "@platform/ui";
import { useCallback, useEffect, useId, useState } from "react";
import { describe } from "./actions";
import { navigate, plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import type { components } from "./api/schema";
import { RaiseTheBudget } from "./budget";
import {
	type CardCounts,
	type CardList,
	type CardsApi,
	CardsPanel,
	countCards,
	summaryLine,
} from "./cards";
import styles from "./project.module.css";
import type { Project } from "./projects";
import { type SchedulesApi, WhenItRunsCard } from "./schedules";
import { settingsPath } from "./sections";
import {
	PRESET_FILLS,
	PRESET_NAMES,
	type ProjectSettings,
	requestOf,
	NOT_READ as SETTINGS_NOT_READ,
	type SettingsApi,
} from "./settings";
import {
	type ProjectSetup,
	type SetupApi,
	SetupPanel,
	type SetupProposal,
	settingUpLine,
	setupProgress,
	setupShows,
} from "./setup";
import type { ValuesApi } from "./values";
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
/** The list's held of a pause, the only holds before the daily budget. */
const PAUSED = ["all_paused", "paused"];

/** The daily budget as the settings summary says it, in millions. */
export const budgetLine = (budget: number | null) =>
	budget === null ? "Off" : `${budget / 1_000_000} M tokens`;

/** Why no worker of the project is handed work, by the list's held. */
export const heldNotice = (held: string): string => {
	switch (held) {
		case "all_paused":
			return "No work is handed out: you paused everything. Continue everything on Running now";
		case "paused":
			return "No work is handed out: you paused it. Continue it at the top of this page";
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
	CardsApi &
	SetupApi &
	ValuesApi &
	SchedulesApi & {
		loadProjects: () => Promise<Project[]>;
		loadWorkers: (id: string) => Promise<WorkerList>;
		/** Makes the project's key, replacing its last, and answers it with
		 * the command that saves it. */
		makeKey: (id: string) => Promise<WorkerKey>;
		/** Pauses the project, or continues it. */
		pauseProject: (id: string, paused: boolean) => Promise<void>;
		/** Pauses the project and stops every run of it under way, answering
		 * how many were told to stop. */
		stopNow: (id: string) => Promise<number>;
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

/** The header's mono caption: the repository, and how long ago it was last
 * read, or that it has not been yet. */
const caption = (project: Project, now: number): string =>
	project.last_read_at === null
		? `${fullName(project)} · not read yet`
		: `${fullName(project)} · read ${duration(Math.max(0, now - Date.parse(project.last_read_at)))} ago`;

/** The stat pills, each a group of the summary line with its dot's tone. */
const PILLS: { group: keyof CardCounts; label: string; tone: string }[] = [
	{ group: "in_progress", label: "In progress", tone: "running" },
	{ group: "needs_you", label: "Needs you", tone: "needs-you" },
	{ group: "done", label: "Done", tone: "done" },
];

const clipboard = (text: string) => navigator.clipboard.writeText(text);

/** The project's header, its cards column and its side column, or a not found state when the
 * caller has no project by that id. Nothing before the projects answer. */
export function ProjectPage({
	id,
	now,
	copy = clipboard,
	back,
	loadProjects,
	loadWorkers,
	makeKey,
	pauseProject,
	stopNow,
	loadSettings,
	saveSettings,
	loadCards,
	loadSetup,
	checkAgain,
	mergeSetup,
	runSetupAgain,
	sendSetupBack,
	loadValues,
	giveValue,
	removeValue,
	loadSchedules,
	saveSchedules,
}: ProjectPageProps) {
	const [project, setProject] = useState<Project | null>();
	const [failed, setFailed] = useState(false);
	// The project's cards, read by the page since its header counts them:
	// undefined before they answer, null when they could not be read.
	const [cards, setCards] = useState<CardList | null>();
	// The project's setup, which decides whether its steps stand in place of
	// its cards; null when it could not be read, and the cards are shown.
	const [setup, setSetup] = useState<ProjectSetup | null>();
	const reloadSetup = useCallback(
		() =>
			loadSetup(id).then(
				(answer) => setSetup(answer),
				() => setSetup(null),
			),
		[id, loadSetup],
	);
	// The settings as saved, which the summary and the preset's badge show,
	// read each time the page opens, so a save on the settings page shows
	// here once back: undefined before they answer, null when they could not
	// be read.
	const [settings, setSettings] = useState<ProjectSettings | null>();
	const acceptProposal = useCallback(
		async (proposal: SetupProposal) => {
			const settings = await loadSettings(id);
			const saved = await saveSettings(
				id,
				requestOf({
					...settings,
					...PRESET_FILLS[proposal.preset],
					wait_paths: proposal.paths,
				}),
			);
			setSettings(saved);
			await reloadSetup();
		},
		[id, loadSettings, saveSettings, reloadSetup],
	);

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
	// Read again once the header paused, continued or stopped it, so its
	// badge and line say what Dev flow holds.
	const reloadProject = useCallback(
		() =>
			loadProjects().then((projects) =>
				setProject(projects.find((p) => p.id === id) ?? null),
			),
		[id, loadProjects],
	);

	useEffect(() => {
		let current = true;
		loadSettings(id).then(
			(answer) => {
				if (current) setSettings(answer);
			},
			() => {
				if (current) setSettings(null);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadSettings]);

	useEffect(() => {
		let current = true;
		loadSetup(id).then(
			(answer) => {
				if (current) setSetup(answer);
			},
			() => {
				if (current) setSetup(null);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadSetup]);

	// The cards are read once the setup says they are shown, and never while
	// the project is setting up, when its steps stand in their place.
	const cardsShown = setup !== undefined && setup?.state !== "setting_up";
	useEffect(() => {
		if (!cardsShown) return;
		let current = true;
		loadCards(id).then(
			(answer) => {
				if (current) setCards(answer);
			},
			() => {
				if (current) setCards(null);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadCards, cardsShown]);

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
			<svg
				className={styles.chevron}
				viewBox="0 0 24 24"
				width="16"
				height="16"
				aria-hidden="true"
			>
				<path d="m15 18-6-6 6-6" />
			</svg>
			Projects
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

	const counts = cardsShown && cards ? countCards(cards) : undefined;
	const preset = settings?.preset;
	// While it is setting up, the header says how far, and whether the next
	// step is the owner's.
	const progress =
		setup?.state === "setting_up" ? setupProgress(setup) : undefined;
	// A setup card holds the page's one primary, its current step's, so the
	// Workers card's offer to make a key steps back to a secondary.
	const setupShown = setup !== undefined && setup !== null && setupShows(setup);
	return (
		<>
			<header className={styles.header}>
				{backLink}
				<div className={styles.heading}>
					<div className={styles.titles}>
						<div className={styles.captionRow}>
							<p className={styles.caption}>{caption(project, now())}</p>
							{progress === undefined ? null : (
								<Status tone={progress.mine ? "needs-you" : "running"}>
									Setting up
								</Status>
							)}
							{preset === undefined ? null : (
								<Badge>{PRESET_NAMES[preset]}</Badge>
							)}
							{project.paused_at === null ? null : <Badge>Paused</Badge>}
						</div>
						<h2 className={styles.name}>{project.name}</h2>
						{progress !== undefined ? (
							<p className={styles.summary}>{settingUpLine(progress)}</p>
						) : counts === undefined ? null : (
							<p className={styles.summary}>{summaryLine(counts)}</p>
						)}
						{project.paused_at === null ? null : (
							<p className={styles.pausedLine}>
								Paused <When at={project.paused_at} />. The run under way
								finishes; nothing new starts.
							</p>
						)}
					</div>
					<div className={styles.end}>
						{counts === undefined ? null : (
							<ul className={styles.pills} aria-label="Cards by group">
								{PILLS.map(({ group, label, tone }) => (
									<li key={group} className={styles.pill}>
										<span
											className={styles.dot}
											data-tone={tone}
											aria-hidden="true"
										/>
										<span className={styles.pillLabel}>{label}</span>
										<span className={styles.pillValue}>{counts[group]}</span>
									</li>
								))}
							</ul>
						)}
						<PauseActions
							project={project}
							pauseProject={pauseProject}
							stopNow={stopNow}
							changed={reloadProject}
						/>
					</div>
				</div>
			</header>
			{/* The cards column, then the side column, which stacks under it on
			    phone. */}
			<div className={styles.columns}>
				<div className={styles.main}>
					{/* While the project is setting up, its steps stand in place of
					    its cards; a registered project whose newer version failed its
					    check shows them above its cards. Nothing before the setup
					    answers. */}
					{setupShown ? (
						<SetupPanel
							id={id}
							setup={setup}
							checkAgain={checkAgain}
							mergeSetup={mergeSetup}
							runSetupAgain={runSetupAgain}
							sendSetupBack={sendSetupBack}
							acceptProposal={acceptProposal}
							copy={copy}
							reload={reloadSetup}
							values={{ loadValues, giveValue, removeValue }}
						/>
					) : null}
					{cardsShown ? <CardsPanel id={id} list={cards} now={now()} /> : null}
				</div>
				<div className={styles.side}>
					{/* Read again once the project is paused or continued, since
					    its held says so. */}
					<WorkersPanel
						key={project.paused_at ?? "running"}
						id={id}
						project={project.name}
						now={now}
						copy={copy}
						loadWorkers={loadWorkers}
						makeKey={makeKey}
						primary={!setupShown}
					/>
					<WhenItRunsCard
						id={id}
						className={styles.card}
						loadSchedules={loadSchedules}
						saveSchedules={saveSchedules}
					/>
					<SettingsSummary id={id} settings={settings} />
				</div>
			</div>
		</>
	);
}

/** The header's actions for the whole project: "Pause", or once paused
 * "Continue", and "Stop now", which asks first, since it ends every run under
 * way at once. Each reads the project again once Dev flow answered. */
function PauseActions({
	project,
	pauseProject,
	stopNow,
	changed,
}: {
	project: Project;
	pauseProject: ProjectApi["pauseProject"];
	stopNow: ProjectApi["stopNow"];
	changed: () => Promise<void>;
}) {
	const [asking, setAsking] = useState(false);
	const [busy, setBusy] = useState(false);
	const [failed, setFailed] = useState<string | null>(null);
	const paused = project.paused_at !== null;

	async function act(what: string, change: () => Promise<unknown>) {
		setBusy(true);
		setFailed(null);
		try {
			await change();
			await changed();
		} catch (error) {
			setFailed(`Could not ${what}: ${describe(error)}`);
		} finally {
			setBusy(false);
		}
	}

	return (
		<div className={styles.actions}>
			<Button
				kind="secondary"
				disabled={busy}
				onClick={() =>
					act(paused ? "continue it" : "pause it", () =>
						pauseProject(project.id, !paused),
					)
				}
			>
				{paused ? "Continue" : "Pause"}
			</Button>
			<Button
				kind="ghost"
				className={styles.stopNow}
				disabled={busy}
				onClick={() => setAsking(true)}
			>
				Stop now
			</Button>
			{failed === null ? null : (
				<p className={styles.actionFailed} role="alert">
					{failed}
				</p>
			)}
			<Dialog
				open={asking}
				kind="destructive"
				title={`Stop ${fullName(project)} now?`}
				confirmLabel="Stop now"
				onConfirm={async () => {
					await act("stop it", () => stopNow(project.id));
					setAsking(false);
				}}
				onCancel={() => setAsking(false)}
			>
				This pauses the project and ends every run under way at once. Each run's
				work is kept on its branch.
			</Dialog>
		</div>
	);
}

/** How the summary says whether pull requests wait, as the panel's select
 * names each choice. */
const WAITS: Record<string, string> = {
	never: "Never",
	sometimes: "Sometimes",
	always: "Always",
};

/** What a project runs on, as its settings page's "Runs on" names it. */
const RUNS_ON: Record<string, string> = {
	subscription: "Your subscription",
	api_key: "The API key only",
};

/** The settings in the side column: the preset, questions before starting,
 * whether pull requests wait, the daily budget and the four labels, as label
 * and value rows,
 * and "Edit settings", which opens the project's settings page. */
function SettingsSummary({
	id,
	settings,
}: {
	id: string;
	settings: ProjectSettings | null | undefined;
}) {
	const rows: [string, string][] = settings
		? [
				["Preset", PRESET_NAMES[settings.preset]],
				["Questions before starting", settings.questions ? "On" : "Off"],
				[
					"Pull requests wait for you",
					WAITS[settings.waits_for_owner] ?? settings.waits_for_owner,
				],
				["Daily budget", budgetLine(settings.daily_token_budget)],
				["Runs on", RUNS_ON[settings.runs_on] ?? settings.runs_on],
				["Pick-up label", settings.pickup_label],
				["Hold label", settings.hold_label],
				["Attention label", settings.attention_label],
				["Review label", settings.review_label],
			]
		: [];
	return (
		<Card
			title="Settings"
			className={styles.card}
			footer={<EditSettings id={id} />}
		>
			{settings === null ? (
				<p className={styles.error} role="alert">
					{SETTINGS_NOT_READ}
				</p>
			) : settings === undefined ? null : (
				<dl className={styles.facts}>
					{rows.map(([label, value]) => (
						<div key={label} className={styles.fact}>
							<dt className={styles.factLabel}>{label}</dt>
							<dd className={styles.factValue}>{value}</dd>
						</div>
					))}
				</dl>
			)}
		</Card>
	);
}

/** "Edit settings": a link to the project's settings page, opened in place
 * on a plain click, drawn as a secondary button. The setup's settings step
 * shows it too. */
export function EditSettings({ id }: { id: string }) {
	const to = settingsPath(id);
	return (
		<a
			className={styles.editSettings}
			href={to}
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					navigate(to);
				}
			}}
		>
			Edit settings
		</a>
	);
}

type WorkersPanelProps = Pick<
	ProjectPageProps,
	"id" | "now" | "loadWorkers" | "makeKey"
> & {
	/** The repository's name, as the rebuild notice names the project. */
	project: string;
	copy: (text: string) => Promise<void>;
	/** Whether "Make a key" may be the page's primary: not while a setup
	 * card holds it. */
	primary: boolean;
};

/** The Workers card: Running while a worker was seen in the last ten
 * minutes, else Idle; why no work is handed out, when none is; how many
 * workers have said hello, in thin numerals, each an inset; the key when it
 * has just been made, or the date it was made; and "Start a worker", which
 * shows the commands that start one, with "Make a key", or "Make a new key"
 * behind a warning once there is one. */
function WorkersPanel({
	id,
	project,
	now,
	copy,
	loadWorkers,
	makeKey,
	primary,
}: WorkersPanelProps) {
	const [list, setList] = useState<WorkerList>();
	const [readFailed, setReadFailed] = useState(false);
	const [key, setKey] = useState<WorkerKey | null>(null);
	const [making, setMaking] = useState(false);
	const [confirming, setConfirming] = useState(false);
	const [error, setError] = useState<string | null>(null);
	// Whether "Start a worker" shows its commands.
	const [starting, setStarting] = useState(false);
	const commands = useId();

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
			// The command that saves the key is shown only now, so the commands
			// open with it rather than wait behind "Start a worker".
			setStarting(true);
			setError(null);
			await reload();
		} catch {
			setError(NOT_MADE);
		}
		setMaking(false);
	};

	const at = now();
	const running =
		list?.workers.some(
			(worker) => at - Date.parse(worker.last_seen_at) < QUIET_MS,
		) ?? false;
	const hasKey = key !== null || (list?.key_made_at ?? null) !== null;
	const actions =
		list === undefined ? undefined : (
			<div className={styles.actions}>
				<Button
					kind="secondary"
					aria-expanded={starting}
					aria-controls={commands}
					onClick={() => setStarting((open) => !open)}
				>
					Start a worker
				</Button>
				{hasKey ? (
					<Button
						kind="ghost"
						disabled={making}
						onClick={() => setConfirming(true)}
					>
						Make a new key
					</Button>
				) : (
					<Button
						kind={primary ? "primary" : "secondary"}
						disabled={making}
						onClick={make}
					>
						Make a key
					</Button>
				)}
			</div>
		);

	return (
		<>
			<Card
				title="Workers"
				className={styles.card}
				aside={
					list === undefined ? undefined : running ? (
						<Status tone="running">Running</Status>
					) : (
						<Status tone="idle">Idle</Status>
					)
				}
				footer={actions}
			>
				<div className={styles.body}>
					{/* The daily budget holds the project after a pause and before
					    every other hold, as the one rule has it, so it is said in
					    their place. */}
					{list?.budget && !PAUSED.includes(list.held ?? "") ? (
						<p className={styles.held}>
							<Status tone="needs-you">No work</Status>
							<span>{list.budget.sentence}</span>
							<RaiseTheBudget id={id} />
						</p>
					) : list?.held ? (
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
					{readFailed ? (
						<p className={styles.error} role="alert">
							{NOT_READ}
						</p>
					) : list === undefined ? null : list.workers.length === 0 ? (
						<p className={styles.muted}>{NO_WORKER}</p>
					) : (
						<>
							<p className={styles.counted}>
								<span className={styles.count}>{list.workers.length}</span>
								<span className={styles.countCaption}>
									{list.workers.length === 1
										? "worker has said hello"
										: "workers have said hello"}
								</span>
							</p>
							<ul className={styles.workers} aria-label="Workers">
								{list.workers.map((worker) => (
									<WorkerRow
										key={worker.id}
										worker={worker}
										project={project}
										now={at}
									/>
								))}
							</ul>
						</>
					)}
					{key !== null ? (
						<ShownKey value={key.key} copy={copy} />
					) : list?.key_made_at ? (
						<p className={styles.line}>
							Key made <When at={list.key_made_at} />.
						</p>
					) : list ? (
						<p className={styles.line}>It has no key yet.</p>
					) : null}
					{list === undefined || !starting ? null : (
						<section
							id={commands}
							className={styles.start}
							aria-label="Start a worker"
						>
							<p className={styles.line}>
								A worker holds this project's key, and asks Dev flow for work
								with it.
							</p>
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
