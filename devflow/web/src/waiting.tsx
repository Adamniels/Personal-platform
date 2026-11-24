// The "Waiting for you" tab: one list of what waits for the owner across his projects, read from
// GET /devflow/api/waiting, the stored picture Dev flow keeps, never from the code host. Grouped by
// kind, each item a card with what he needs to judge it. Every text in an item came from the code
// host and is shown as text. See "The pages" and "What waits" in devflow/README.md.
import { Button, EmptyState, Status } from "@platform/ui";
import { useQuery } from "@tanstack/react-query";
import {
	Fragment,
	type MouseEvent,
	type ReactNode,
	useId,
	useState,
} from "react";
import { type ActionsApi, ItemActions, type LinkState } from "./actions";
import { navigate, plainClick, useSearch } from "./address";
import type { GetAnswer } from "./api/client";
import { DecisionIssue } from "./decision-issue";
import {
	count,
	Detail,
	decisionsTile,
	short,
	type Tile,
	Tiles,
} from "./detail";
import { Inline } from "./format";
import { QuestionCard } from "./question-card";
import { RefusedCard } from "./refused-card";
import { Screenshots, waitingScreenshot } from "./screenshots";
import {
	setupFailedTitle,
	setupRunTitle,
	WaitingSetup,
	WaitingSetupRun,
} from "./setup";
import { StoppedProject, stoppedTitle } from "./stopped";
import { FailingProject, StoppedCard } from "./stopped-card";
import styles from "./waiting.module.css";
import { WaitingCard } from "./waiting-card";
import { WaitingFeature } from "./waiting-feature";

export type WaitingList = GetAnswer<"/waiting">;
export type WaitingItem = WaitingList["items"][number];
export type OwnItem = WaitingList["own_items"][number];
export type CardItem = WaitingList["cards"][number];
export type StoppedCardItem = WaitingList["stopped_cards"][number];
export type RefusedCardItem = WaitingList["refused_cards"][number];
export type QuestionCardItem = WaitingList["question_cards"][number];
export type FailingProjectItem = WaitingList["failing_projects"][number];
type Decision = WaitingItem["decisions"][number];
type Freshness = WaitingList["projects"][number];

/** How the tab reads the list and asks for a project to be read again,
 * Dev flow's API in the page and a test's own in a test. */
export type WaitingApi = {
	loadWaiting: () => Promise<WaitingList>;
	refresh: (projectId: string) => Promise<void>;
};

export const WAITING = ["devflow", "waiting"] as const;

/** How often the list is read again while the tab is visible. */
const EVERY = 60_000;

/** The list, read as the page loads, every minute while the page is visible,
 * and when the window regains focus. */
export function useWaiting(load: WaitingApi["loadWaiting"]) {
	return useQuery({
		queryKey: WAITING,
		queryFn: load,
		refetchInterval: EVERY,
		refetchIntervalInBackground: false,
		refetchOnWindowFocus: true,
	});
}

/** How an item's row marks its state: the dot's colour, from the status
 * tones of "Badge and status label" in design/DESIGN.md. */
type Tone = "needs-you" | "failed" | "running";

/** One waiting item, of any kind: its key in the address, its row in the
 * queue, and its detail. */
type Entry = {
	/** Names exactly this item, one pattern per kind; see "The pages" in
	 * devflow/README.md. */
	key: string;
	title: string;
	/** The meta line's parts: the project, the number and the one fact that
	 * matters. */
	meta: string[];
	tone: Tone;
	/** The item, rendered by the component its kind already has. */
	detail: (act: Act) => ReactNode;
};

type Group = {
	label: string;
	/** Questions before starting asks for the owner; Sent back is the
	 * runner's, so it steps back. */
	look?: "asks" | "muted";
	entries: Entry[];
};

/** What an item's actions need from the tab. */
type Act = {
	link: LinkState;
	api: ActionsApi;
	reload: () => Promise<unknown>;
};

const plural = (count: number, word: string) =>
	`${count} ${word}${count === 1 ? "" : "s"}`;

/** The eight groups, in their order: Dev flow's cards that wait for the
 * owner's answers before they start; Dev flow's waiting cards and the
 * waiting items not sent back; Dev flow's features ready for the owner's
 * review, or waiting for him; what stopped, Dev flow's stopped projects
 * first, then the projects that cannot run their cards, its stopped cards and
 * the runner's stopped items not sent back; Dev flow's projects whose setup
 * run waits or stopped, and those whose newest check of their setup failed;
 * Dev flow's cards refused as too thin; the decisions to look at on cards Dev
 * flow merged; and every item sent back. Only groups with an item. */
function groups(list: WaitingList, name: (item: WaitingItem) => string) {
	const sorted = [...list.items].sort(
		(a, b) => compare(name(a), name(b)) || a.number - b.number,
	);
	const runner = (each: WaitingItem): Entry => ({
		key: each.id,
		title: each.title,
		meta: [
			each.project_name,
			`#${each.number}`,
			...(each.sent_back
				? ["sent back"]
				: each.kind === "stopped"
					? ["stopped"]
					: each.decisions.length > 0
						? [plural(each.decisions.length, "decision")]
						: []),
		],
		tone: each.sent_back
			? "running"
			: each.kind === "stopped"
				? "failed"
				: "needs-you",
		detail: (act) => (
			<Item item={each} project={name(each)} primary act={act} />
		),
	});
	const all: Group[] = [
		{
			label: "Questions before starting",
			look: "asks",
			entries: list.question_cards.map((card) => ({
				key: `questions/${card.project_id}/${card.number}`,
				title: card.title,
				meta: [
					card.project_name,
					`card #${card.number}`,
					plural(card.questions.length, "question"),
				],
				tone: "needs-you",
				detail: (act) => (
					<QuestionCard
						card={card}
						link={act.link}
						api={act.api}
						reload={act.reload}
						linkable={linkable}
					/>
				),
			})),
		},
		{
			label: "Pull requests waiting",
			entries: [
				...list.cards.map(
					(card): Entry => ({
						key: `card/${card.project_id}/${card.number}`,
						title: card.title,
						meta: [
							card.project_name,
							`#${card.pull_request_number}`,
							...(card.decisions.length > 0
								? [plural(card.decisions.length, "decision")]
								: []),
						],
						tone: "needs-you",
						detail: (act) => (
							<WaitingCard
								card={card}
								primary
								link={act.link}
								api={act.api}
								reload={act.reload}
							/>
						),
					}),
				),
				...sorted
					.filter((each) => each.kind === "waiting" && !each.sent_back)
					.map(runner),
			],
		},
		{
			label: "Features ready for your review",
			entries: list.features.map((feature) => ({
				key: `feature/${feature.project_id}/${feature.number}`,
				title: feature.title,
				meta: [
					feature.project_name,
					`feature #${feature.number}`,
					...(feature.unanswered > 0
						? [`${feature.unanswered} unanswered`]
						: []),
				],
				tone: "needs-you",
				detail: (act) => (
					<WaitingFeature
						feature={feature}
						link={act.link}
						api={act.api}
						reload={act.reload}
					/>
				),
			})),
		},
		{
			label: "Stopped",
			entries: [
				...list.own_items
					.filter((each) => each.kind === "project_stopped")
					.map(
						(stop): Entry => ({
							key: `stopped/${stop.project_id}`,
							title: stoppedTitle(stop.branch),
							meta: [stop.project_name, stop.commit.slice(0, 7), "stopped"],
							tone: "failed",
							detail: (act) => (
								<StoppedProject
									stop={stop}
									api={act.api}
									reload={act.reload}
									linkable={linkable}
								/>
							),
						}),
					),
				...list.failing_projects.map(
					(project): Entry => ({
						key: `failing/${project.project_id}`,
						title: project.message,
						meta: [project.project_name, "cannot run"],
						tone: "failed",
						detail: () => <FailingProject project={project} />,
					}),
				),
				...list.stopped_cards.map(
					(card): Entry => ({
						key: `card/${card.project_id}/${card.number}`,
						title: card.title,
						meta: [card.project_name, `card #${card.number}`, "stopped"],
						tone: "failed",
						detail: (act) => (
							<StoppedCard
								card={card}
								link={act.link}
								api={act.api}
								reload={act.reload}
								linkable={linkable}
							/>
						),
					}),
				),
				...sorted
					.filter((each) => each.kind === "stopped" && !each.sent_back)
					.map(runner),
			],
		},
		{
			label: "Setup",
			entries: [
				...list.setup_runs.map(
					(project): Entry => ({
						key: `setup-run/${project.project_id}`,
						title: setupRunTitle(project),
						meta: [
							project.project_name,
							...(project.pull_request_number === null
								? []
								: [`#${project.pull_request_number}`]),
							project.state === "stopped" ? "stopped" : "waits for you",
						],
						tone: project.state === "stopped" ? "failed" : "needs-you",
						detail: () => <WaitingSetupRun project={project} />,
					}),
				),
				...list.setup_projects.map(
					(project): Entry => ({
						key: `setup-check/${project.project_id}`,
						title: setupFailedTitle(project),
						meta: [project.project_name, `exit ${project.exit_code}`],
						tone: "failed",
						detail: () => <WaitingSetup project={project} />,
					}),
				),
			],
		},
		{
			label: "Refused",
			entries: list.refused_cards.map((card) => ({
				key: `refused/${card.project_id}/${card.number}`,
				title: card.title,
				meta: [card.project_name, `card #${card.number}`, "too thin"],
				tone: "failed",
				detail: () => <RefusedCard card={card} linkable={linkable} />,
			})),
		},
		{
			label: "Decisions to look at",
			entries: list.decisions.map((decision) => ({
				key: `decision/${decision.id}`,
				title: decision.chosen,
				meta: [
					decision.project_name,
					`issue #${decision.issue_number}`,
					`merged in #${decision.pull_request_number}`,
				],
				tone: "needs-you",
				detail: () => <DecisionIssue decision={decision} />,
			})),
		},
		{
			label: "Sent back, the runner has it",
			look: "muted",
			entries: sorted.filter((each) => each.sent_back).map(runner),
		},
	];
	return all.filter((group) => group.entries.length > 0);
}

const compare = new Intl.Collator("en", { sensitivity: "base" }).compare;

const fullName = (project: { owner: string; name: string }) =>
	`${project.owner}/${project.name}`;

export const NOT_READ = "What waits for you could not be read. Try again.";
export const RELINK =
	"Your GitHub link has expired. You can read, but not merge or comment.";
const PROJECTS = "/devflow/projects";

export type WaitingProps = {
	/** The answer, undefined before it arrives or when it cannot be read. */
	list: WaitingList | undefined;
	/** Whether reading it failed, with nothing read before. */
	failed: boolean;
	/** Asks for one project to be read now. */
	refresh: WaitingApi["refresh"];
	/** Reads the list again. */
	reload: () => Promise<unknown>;
	/** The time, in milliseconds since the epoch. */
	now: () => number;
	/** The caller's GitHub link, which merging and commenting need. */
	link: LinkState;
	/** Merges an item and sends one back. */
	actions: ActionsApi;
};

/** The tab: the expired link at the top, the age of the picture with
 * Refresh, and the groups, each a region named by its heading; or the empty
 * state that says why there is nothing. Nothing before the answer. */
export function Waiting({
	list,
	failed,
	refresh,
	reload,
	now,
	link,
	actions,
}: WaitingProps) {
	if (list === undefined) {
		return failed ? (
			<p className={styles.error} role="alert">
				{NOT_READ}
			</p>
		) : null;
	}

	const projects = new Map(list.projects.map((each) => [each.id, each]));
	// The answer names each item's project; the repository alone if it ever
	// does not.
	const name = (item: WaitingItem) => {
		const project: Freshness | undefined = projects.get(item.project_id);
		return project ? fullName(project) : item.project_name;
	};
	const waiting = groups(list, name);

	return (
		<div className={styles.list}>
			{list.needs_relink ? <Relink /> : null}
			{list.projects.length === 0 ? (
				<EmptyState
					title="Nothing waits for you"
					detail="Connect a repository under Projects to see what waits for you."
				/>
			) : (
				<>
					<Age
						projects={list.projects}
						now={now()}
						refresh={async () => {
							await Promise.allSettled(
								list.projects.map((project) => refresh(project.id)),
							);
							await reload();
						}}
					/>
					{waiting.length === 0 ? (
						<EmptyState
							title="Nothing waits for you"
							detail="Your agents have nothing for you to decide."
						/>
					) : (
						<Inbox groups={waiting} act={{ link, api: actions, reload }} />
					)}
				</>
			)}
		</div>
	);
}

/** The owner's link has expired: what he can still do, and where to link it
 * again. */
function Relink() {
	const go = (event: MouseEvent<HTMLAnchorElement>) => {
		event.preventDefault();
		navigate(PROJECTS);
	};
	return (
		<p className={styles.attention}>
			{RELINK}{" "}
			<a className={styles.link} href={PROJECTS} onClick={go}>
				Go to Projects
			</a>
		</p>
	);
}

/** How old the picture is: a line for each project that is stale or
 * failing, in the attention style, or one muted line from the oldest read
 * when every project is fine; and Refresh beside it. */
function Age({
	projects,
	now,
	refresh,
}: {
	projects: Freshness[];
	now: number;
	refresh: () => Promise<void>;
}) {
	const [refreshing, setRefreshing] = useState(false);
	const lines = projects.flatMap((project) => [
		...(project.stale ? [staleLine(project, now)] : []),
		...(project.failing
			? [
					`${fullName(project)}: GitHub could not be read, showing what was last seen`,
				]
			: []),
	]);
	const oldest = Math.min(
		...projects.map((project) => Date.parse(project.last_success_at ?? "")),
	);

	return (
		<div className={styles.age}>
			{lines.length > 0 ? (
				<div className={`${styles.attention} ${styles.lines}`}>
					{lines.map((line) => (
						<p key={line} className={styles.line}>
							{line}
						</p>
					))}
				</div>
			) : Number.isNaN(oldest) ? null : (
				<p className={styles.updated}>Updated {duration(now - oldest)} ago</p>
			)}
			<Button
				disabled={refreshing}
				onClick={async () => {
					setRefreshing(true);
					try {
						await refresh();
					} finally {
						setRefreshing(false);
					}
				}}
			>
				Refresh
			</Button>
		</div>
	);
}

function staleLine(project: Freshness, now: number): string {
	if (project.last_success_at === null)
		return `${fullName(project)} has not been read yet`;
	const since = now - Date.parse(project.last_success_at);
	return `${fullName(project)} has not been updated for ${duration(since)}`;
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** A length of time in words, to the largest whole unit: "less than a
 * minute", minutes below an hour, hours below two days, then days. */
export function duration(ms: number): string {
	const count = (n: number, unit: string) =>
		`${n} ${unit}${n === 1 ? "" : "s"}`;
	if (ms < MINUTE) return "less than a minute";
	if (ms < HOUR) return count(Math.floor(ms / MINUTE), "minute");
	if (ms < 2 * DAY) return count(Math.floor(ms / HOUR), "hour");
	return count(Math.floor(ms / DAY), "day");
}

/** The inbox: the queue of every waiting item, grouped, and the chosen one
 * in full beside it. The address names the chosen item, `?item=<key>`:
 * choosing one pushes its address, and Back and Forward bring the one before
 * back. An address that names no item shows the first. The detail is keyed by
 * the item, so what was typed in one is not carried to the next. */
function Inbox({ groups, act }: { groups: Group[]; act: Act }) {
	const entries = groups.flatMap((group) => group.entries);
	const asked = new URLSearchParams(useSearch()).get("item");
	const chosen = entries.find((entry) => entry.key === asked) ?? entries[0];
	if (chosen === undefined) return null;
	const muted = groups.some(
		(group) => group.look === "muted" && group.entries.includes(chosen),
	);
	return (
		// On a phone the inbox is two screens: the queue, and the item the
		// address names on a screen of its own.
		<div
			className={styles.inbox}
			data-open={chosen.key === asked ? "" : undefined}
		>
			<Queue
				groups={groups}
				chosen={chosen.key}
				choose={(key) => navigate(itemPath(key))}
			/>
			<div
				className={muted ? `${styles.detail} ${styles.muted}` : styles.detail}
			>
				<Back position={entries.indexOf(chosen) + 1} of={entries.length} />
				<Fragment key={chosen.key}>{chosen.detail(act)}</Fragment>
			</div>
		</div>
	);
}

/** The way back to the queue from an item's screen, and where the item is
 * in it; shown on a phone only. */
function Back({ position, of }: { position: number; of: number }) {
	return (
		<p className={styles.back}>
			<a
				className={styles.backLink}
				href={TAB}
				onClick={(event) => {
					if (plainClick(event)) {
						event.preventDefault();
						navigate(TAB);
					}
				}}
			>
				<svg
					aria-hidden="true"
					viewBox="0 0 24 24"
					width="20"
					height="20"
					fill="none"
					stroke="currentColor"
					strokeWidth="2"
					strokeLinecap="round"
					strokeLinejoin="round"
				>
					<path d="m15 18-6-6 6-6" />
				</svg>
				Waiting for you
			</a>{" "}
			<span className={styles.position}>
				{position} of {of}
			</span>
		</p>
	);
}

const TAB = "/devflow";

/** The address of an item: the tab's own, with the item's key, its slashes
 * kept so it reads as the pattern is written. */
export const itemPath = (key: string) =>
	`${TAB}?item=${encodeURIComponent(key).replaceAll("%2F", "/")}`;

/** The queue: a card holding every group, its heading and its count over
 * its rows. No button: the view's one primary is in the detail. */
function Queue({
	groups,
	chosen,
	choose,
}: {
	groups: Group[];
	chosen: string;
	choose: (key: string) => void;
}) {
	return (
		<nav aria-label="The queue" className={styles.queue}>
			{groups.map((group) => (
				<QueueGroup
					key={group.label}
					group={group}
					chosen={chosen}
					choose={choose}
				/>
			))}
		</nav>
	);
}

function QueueGroup({
	group,
	chosen,
	choose,
}: {
	group: Group;
	chosen: string;
	choose: (key: string) => void;
}) {
	const id = useId();
	const look =
		group.look === "asks"
			? styles.asks
			: group.look === "muted"
				? styles.muted
				: undefined;
	return (
		<section
			aria-labelledby={id}
			className={look ? `${styles.group} ${look}` : styles.group}
		>
			<h2 className={styles.heading}>
				<span id={id}>{group.label}</span>{" "}
				<span className={styles.count}>{group.entries.length}</span>
			</h2>
			<ul className={styles.rows}>
				{group.entries.map((entry) => (
					<li key={entry.key}>
						<a
							className={styles.row}
							href={itemPath(entry.key)}
							aria-current={entry.key === chosen ? "true" : undefined}
							onClick={(event) => {
								if (plainClick(event)) {
									event.preventDefault();
									choose(entry.key);
								}
							}}
						>
							<span className={`${styles.dot} ${styles[entry.tone]}`} />
							<span className={styles.what}>
								<span className={styles.title}>{entry.title}</span>{" "}
								<span className={styles.meta}>{entry.meta.join(" · ")}</span>
							</span>
						</a>
					</li>
				))}
			</ul>
		</section>
	);
}

/** A runner item's tiles: why it waits, what the reviewer said of its head
 * and the decisions. Waiting, it was approved for its head commit and handed
 * to the owner; stopped, the runner gave up on it. */
export function runnerTiles(item: WaitingItem): Tile[] {
	const waiting = item.kind === "waiting";
	return [
		item.sent_back
			? {
					label: "Why it waits",
					tone: "running",
					status: "Sent back",
					line: "the runner has it",
				}
			: waiting
				? {
						label: "Why it waits",
						tone: "needs-you",
						status: item.decisions.length > 0 ? "Your decision" : "Your merge",
						line: item.can_merge
							? "ci-ok and review-ok green"
							: "checks not green yet",
					}
				: {
						label: "Why it waits",
						tone: "failed",
						status: "Stopped",
						line: "the runner gave up",
					},
		waiting
			? {
					label: "The reviewer",
					tone: "done",
					status: "Approved",
					line: `on ${short(item.head_commit)} · ${count(item.reviewer_view.length, "point")} for you`,
				}
			: {
					label: "The reviewer",
					tone: "failed",
					status: "Not approved",
					line: `on ${short(item.head_commit)}`,
				},
		decisionsTile(item.decisions.length, item.other_decisions),
	];
}

/** One runner item in the detail's frame: where it is, its title, its
 * summary, its tiles, why it waits in the runner's words, the decisions,
 * the reviewer's points and its screenshots, then its actions. */
function Item({
	item,
	project,
	primary,
	act,
}: {
	item: WaitingItem;
	project: string;
	primary: boolean;
	act: Act;
}) {
	return (
		<Detail
			title={item.title}
			where={[project, `pull request #${item.number}`, short(item.head_commit)]}
			status={<ItemStatus item={item} />}
			needsYou={item.kind === "waiting" && !item.sent_back}
			lede={item.summary === "" ? null : <Inline text={item.summary} />}
			actions={
				<ItemActions
					item={item}
					primary={primary}
					link={act.link}
					api={act.api}
					reload={act.reload}
					open={
						linkable(item.web_url) ? (
							<a
								className={styles.open}
								href={item.web_url}
								target="_blank"
								rel="noreferrer"
							>
								Open on GitHub
							</a>
						) : null
					}
				/>
			}
		>
			<Tiles tiles={runnerTiles(item)} />
			<Reason item={item} />
			{item.kind === "waiting" && item.decisions.length > 0 ? (
				<Decisions decisions={item.decisions} others={item.other_decisions} />
			) : null}
			{item.kind === "waiting" && item.reviewer_view.length > 0 ? (
				<Reviewer points={item.reviewer_view} />
			) : null}
			<Screenshots
				address={(name) => waitingScreenshot(item.id, name)}
				groups={item.screenshots}
			/>
		</Detail>
	);
}

/** Whether the code host's address for an item may be a link: https only,
 * since the address is data, and a link to any other scheme could run
 * script in the page or open something that is not the code host. */
export function linkable(address: string): boolean {
	try {
		return new URL(address).protocol === "https:";
	} catch {
		return false;
	}
}

function ItemStatus({ item }: { item: WaitingItem }) {
	if (item.sent_back) return <Status tone="running">Sent back</Status>;
	if (item.kind === "stopped") return <Status tone="failed">Stopped</Status>;
	return <Status tone="needs-you">Waiting for you</Status>;
}

/** Why it is the owner's turn, in the runner's words. */
function Reason({ item }: { item: WaitingItem }) {
	return (
		<div className={styles.block}>
			<h3 className={styles.label}>
				{item.kind === "stopped" ? "Why it stopped" : "In the runner's words"}
			</h3>
			<p className={styles.text}>
				<Inline text={item.reason} />
			</p>
		</div>
	);
}

/** The decisions the agent marked for the owner, each what was chosen, what
 * else was possible and why, and a count of the others, which are on the
 * code host. */
export function Decisions({
	decisions,
	others,
}: {
	decisions: Decision[];
	others: number;
}) {
	const id = useId();
	return (
		<div className={styles.block}>
			<h3 id={id} className={styles.label}>
				The agent decided
			</h3>
			<ul aria-labelledby={id} className={styles.decisions}>
				{decisions.map((decision, index) => (
					// A decision has no id of its own, and the list is
					// replaced whole when it is read again.
					// biome-ignore lint/suspicious/noArrayIndexKey: see above
					<li key={index} className={styles.decision}>
						<p className={styles.chosen}>
							<Inline text={decision.chosen} />
						</p>
						{decision.alternative === null ? null : (
							<p className={styles.text}>
								<span className={styles.prefix}>Instead of</span>{" "}
								<Inline text={decision.alternative} />
							</p>
						)}
						{decision.why === null ? null : (
							<p className={styles.text}>
								<span className={styles.prefix}>Because</span>{" "}
								<Inline text={decision.why} />
							</p>
						)}
					</li>
				))}
			</ul>
			{others > 0 ? (
				<p className={styles.more}>and {others} more it did not need you for</p>
			) : null}
		</div>
	);
}

/** What the reviewer wrote for the owner, a point each, on an inset row. */
function Reviewer({ points }: { points: string[] }) {
	const id = useId();
	return (
		<div className={styles.block}>
			<h3 id={id} className={styles.label}>
				The reviewer
			</h3>
			<ul aria-labelledby={id} className={styles.points}>
				{points.map((point, index) => (
					// biome-ignore lint/suspicious/noArrayIndexKey: points have no id, and are replaced whole
					<li key={index} className={`${styles.text} ${styles.point}`}>
						<Inline text={point} />
					</li>
				))}
			</ul>
		</div>
	);
}
