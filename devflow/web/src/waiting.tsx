// The "Waiting for you" tab: one list of what waits for the owner across his projects, read from
// GET /devflow/api/waiting, the stored picture Dev flow keeps, never from the code host. Grouped by
// kind, each item a card with what he needs to judge it. Every text in an item came from the code
// host and is shown as text. See "The pages" and "What waits" in devflow/README.md.
import { Card, Status } from "@platform/ui";
import { useQuery } from "@tanstack/react-query";
import { useId } from "react";
import type { GetAnswer } from "./api/client";
import { Inline } from "./format";
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

export type WaitingProps = {
	list: WaitingList;
};

/** The groups of the list, each a region named by its heading. */
export function Waiting({ list }: WaitingProps) {
	const projects = new Map(list.projects.map((each) => [each.id, each]));
	// The answer names each item's project; the repository alone if it ever
	// does not.
	const name = (item: WaitingItem) => {
		const project: Freshness | undefined = projects.get(item.project_id);
		return project ? fullName(project) : item.project_name;
	};

	return (
		<div className={styles.list}>
			{groups(list.items, name).map((group) => (
				<GroupSection key={group.label} group={group} name={name} />
			))}
		</div>
	);
}

function GroupSection({
	group,
	name,
}: {
	group: Group;
	name: (item: WaitingItem) => string;
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
			{group.items.map((item) => (
				<Item key={item.id} item={item} project={name(item)} />
			))}
		</section>
	);
}

/** One item: the card's header holds its title and its status, the line
 * under it the project and the number, then what the owner needs to judge
 * it, then its actions. */
function Item({ item, project }: { item: WaitingItem; project: string }) {
	const waiting = item.kind === "waiting" && !item.sent_back;
	return (
		<Card
			title={item.title}
			attention={waiting}
			aside={<ItemStatus item={item} />}
			className={styles.item}
			footer={
				<div className={styles.actions}>
					<a
						className={styles.open}
						href={item.web_url}
						target="_blank"
						rel="noreferrer"
					>
						Open on GitHub
					</a>
				</div>
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
			</div>
		</Card>
	);
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
