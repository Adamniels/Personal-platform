// The "Waiting for you" tab: one list of what waits for the owner across his projects, read from
// GET /devflow/api/waiting, the stored picture Dev flow keeps, never from the code host. Grouped by
// kind, each item a card with what he needs to judge it. Every text in an item came from the code
// host and is shown as text. See "The pages" and "What waits" in devflow/README.md.
import { Button, Card, EmptyState, Status } from "@platform/ui";
import { useQuery } from "@tanstack/react-query";
import { type MouseEvent, useId, useState } from "react";
import { type ActionsApi, ItemActions, type LinkState } from "./actions";
import { navigate } from "./address";
import type { GetAnswer } from "./api/client";
import { Inline } from "./format";
import { Screenshots } from "./screenshots";
import styles from "./waiting.module.css";

export type WaitingList = GetAnswer<"/waiting">;
export type WaitingItem = WaitingList["items"][number];
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

type Group = {
	label: string;
	items: WaitingItem[];
	muted?: boolean;
	/** Whether the first item's Merge is the view's one primary button,
	 * design/DESIGN.md's rule; the others are secondary. */
	primaryFirst?: boolean;
};

/** What an item's actions need from the tab. */
type Act = {
	link: LinkState;
	api: ActionsApi;
	reload: () => Promise<unknown>;
};

/** The three groups, in their order: waiting and stopped items not sent
 * back, and every item sent back. */
function groups(items: WaitingItem[], name: (item: WaitingItem) => string) {
	const sorted = [...items].sort(
		(a, b) => compare(name(a), name(b)) || a.number - b.number,
	);
	const all: Group[] = [
		{
			label: "Pull requests waiting",
			items: sorted.filter(
				(item) => item.kind === "waiting" && !item.sent_back,
			),
			primaryFirst: true,
		},
		{
			label: "Stopped",
			items: sorted.filter(
				(item) => item.kind === "stopped" && !item.sent_back,
			),
		},
		{
			label: "Sent back, the runner has it",
			items: sorted.filter((item) => item.sent_back),
			muted: true,
		},
	];
	return all.filter((group) => group.items.length > 0);
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
					{list.items.length === 0 ? (
						<EmptyState
							title="Nothing waits for you"
							detail="Your agents have nothing for you to decide."
						/>
					) : (
						groups(list.items, name).map((group) => (
							<GroupSection
								key={group.label}
								group={group}
								name={name}
								act={{ link, api: actions, reload }}
							/>
						))
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

function GroupSection({
	group,
	name,
	act,
}: {
	group: Group;
	name: (item: WaitingItem) => string;
	act: Act;
}) {
	const id = useId();
	return (
		<section
			aria-labelledby={id}
			className={group.muted ? `${styles.group} ${styles.muted}` : styles.group}
		>
			<h2 className={styles.heading}>
				<span id={id}>{group.label}</span>{" "}
				<span className={styles.count}>{group.items.length}</span>
			</h2>
			{group.items.map((item, index) => (
				<Item
					key={item.id}
					item={item}
					project={name(item)}
					primary={group.primaryFirst === true && index === 0}
					act={act}
				/>
			))}
		</section>
	);
}

/** One item: the card's header holds its title and its status, the line
 * under it the project and the number, then what the owner needs to judge
 * it, its screenshots last, then its actions, before Open on GitHub. */
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
	const waiting = item.kind === "waiting" && !item.sent_back;
	return (
		<Card
			title={item.title}
			attention={waiting}
			aside={<ItemStatus item={item} />}
			className={styles.item}
			footer={
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
			<div className={styles.body}>
				<p className={styles.where}>
					<span>{project}</span> <span>#{item.number}</span>
				</p>
				{item.summary === "" ? null : (
					<p className={styles.summary}>
						<Inline text={item.summary} />
					</p>
				)}
				{item.kind === "waiting" && item.decisions.length > 0 ? (
					<Decisions item={item} />
				) : (
					<Reason item={item} />
				)}
				{item.kind === "waiting" && item.reviewer_view.length > 0 ? (
					<Reviewer points={item.reviewer_view} />
				) : null}
				<Screenshots item={item.id} groups={item.screenshots} />
			</div>
		</Card>
	);
}

/** Whether the code host's address for an item may be a link: https only,
 * since the address is data, and a link to any other scheme could run
 * script in the page or open something that is not the code host. */
function linkable(address: string): boolean {
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
				{item.kind === "stopped" ? "Why it stopped" : "Why it waits"}
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
function Decisions({ item }: { item: WaitingItem }) {
	const id = useId();
	return (
		<div className={styles.block}>
			<h3 id={id} className={styles.label}>
				The agent decided
			</h3>
			<ul aria-labelledby={id} className={styles.decisions}>
				{item.decisions.map((decision, index) => (
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
			{item.other_decisions > 0 ? (
				<p className={styles.more}>
					and {item.other_decisions} more it did not need you for
				</p>
			) : null}
		</div>
	);
}

/** What the reviewer wrote for the owner, a point each. */
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
					<li key={index} className={styles.text}>
						<Inline text={point} />
					</li>
				))}
			</ul>
		</div>
	);
}
