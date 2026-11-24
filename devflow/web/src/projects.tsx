// The projects on the Projects tab, under Connections: the heading with "Connect a repository",
// connecting one from those the GitHub App reaches for the owner, and a card per connected
// repository drawn from its summary, with an arrow link to its page and Disconnect behind a
// confirmation. See "The pages" and "Connecting a repository" in devflow/README.md.
import {
	Badge,
	Button,
	Card,
	Dialog,
	EmptyState,
	Status,
	Table,
} from "@platform/ui";
import { useCallback, useEffect, useId, useState } from "react";
import { navigate, plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import { ArrowUpRightIcon } from "./icons";
import { QUIET_MS } from "./project";
import styles from "./projects.module.css";
import { projectPath } from "./sections";
import { duration } from "./waiting";

export type Project = GetAnswer<"/projects">["projects"][number];
export type Connectable = GetAnswer<"/github/repositories">;
type Installation = Connectable["installations"][number];
type Repository = Installation["repositories"][number];

export const NOT_INSTALLED =
	"The GitHub App is not installed on any repository you can reach.";
export const NOT_READ = "Your repositories could not be read. Try again.";
export const NOT_CONNECTED = "The repository was not connected. Try again.";
export const NOT_DISCONNECTED = "The project was not disconnected. Try again.";

/** How the tab reads and changes projects, Dev flow's API in the page and
 * a test's own in a test. */
export type ProjectsApi = {
	loadProjects: () => Promise<Project[]>;
	loadRepositories: () => Promise<Connectable>;
	connect: (installationId: number, repositoryId: number) => Promise<void>;
	disconnect: (id: string) => Promise<void>;
};

export type ProjectsProps = ProjectsApi & {
	/** Whether GitHub is linked, without which nothing can be connected. */
	linked: boolean;
	/** The time, in milliseconds since the epoch, a worker's last word is
	 * told by; the clock unless a test passes its own. */
	now?: () => number;
};

const fullName = (item: { owner: string; name: string }) =>
	`${item.owner}/${item.name}`;

/** The section: its heading with "Connect a repository", the view's one
 * primary, the list to choose from when it is open, and a card per project,
 * or an empty state when there are none. Nothing of the list before it is
 * read. */
export function Projects({
	linked,
	now = Date.now,
	loadProjects,
	loadRepositories,
	connect,
	disconnect,
}: ProjectsProps) {
	const [projects, setProjects] = useState<Project[]>();
	const [choosing, setChoosing] = useState(false);
	const [leaving, setLeaving] = useState<Project | null>(null);
	const [error, setError] = useState<string | null>(null);

	const reload = useCallback(
		() => loadProjects().then(setProjects, () => setProjects(undefined)),
		[loadProjects],
	);

	useEffect(() => {
		let current = true;
		loadProjects().then(
			(answer) => {
				if (current) setProjects(answer);
			},
			() => {
				if (current) setProjects(undefined);
			},
		);
		return () => {
			current = false;
		};
	}, [loadProjects]);

	const confirmDisconnect = async () => {
		if (leaving === null) return;
		try {
			await disconnect(leaving.id);
			setError(null);
			await reload();
		} catch {
			setError(NOT_DISCONNECTED);
		}
		setLeaving(null);
	};

	const heading = useId();

	return (
		<section className={styles.section} aria-labelledby={heading}>
			<div className={styles.head}>
				<h2 id={heading}>Projects</h2>
				<Button
					kind="primary"
					disabled={!linked || choosing}
					onClick={() => setChoosing(true)}
				>
					Connect a repository
				</Button>
			</div>
			{choosing ? (
				<Chooser
					load={loadRepositories}
					connect={async (installation, repository) => {
						await connect(installation, repository);
						setChoosing(false);
						await reload();
					}}
					close={() => setChoosing(false)}
				/>
			) : null}
			{error === null ? null : (
				<p className={styles.error} role="alert">
					{error}
				</p>
			)}
			{projects === undefined ? null : projects.length === 0 ? (
				// While choosing, the list to choose from says what comes next.
				choosing ? null : (
					<EmptyState
						title="No project connected yet"
						detail="Connect a repository to see what waits for you in it."
					/>
				)
			) : (
				<div className={styles.cards}>
					{projects.map((project) => (
						<ProjectCard
							key={project.id}
							project={project}
							now={now()}
							disconnect={() => setLeaving(project)}
						/>
					))}
				</div>
			)}
			<Dialog
				open={leaving !== null}
				title={leaving === null ? "" : `Disconnect ${fullName(leaving)}?`}
				confirmLabel="Disconnect"
				kind="destructive"
				onConfirm={confirmDisconnect}
				onCancel={() => setLeaving(null)}
			>
				Dev flow forgets this project and everything it read for it. Nothing
				changes on GitHub.
			</Dialog>
		</section>
	);
}

/** A link that opens an address of the page in place on a plain click, and
 * lets the browser have any other, a new tab among them. */
function PageLink({
	to,
	className,
	label,
	children,
}: {
	to: string;
	className?: string;
	label?: string;
	children: React.ReactNode;
}) {
	return (
		<a
			className={className}
			href={to}
			aria-label={label}
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					navigate(to);
				}
			}}
		>
			{children}
		</a>
	);
}

/** A project's card, from its summary: the full name as its label with the
 * arrow link to its page; the name as the hero label beside how many of its
 * cards need the owner; then at most three inset rows, what is building, its
 * open feature and its workers; and Disconnect at its foot. A project whose
 * card needs the owner takes the accent border. */
function ProjectCard({
	project,
	now,
	disconnect,
}: {
	project: Project;
	now: number;
	disconnect: () => void;
}) {
	const name = fullName(project);
	const { cards } = project.summary;
	const total = cards.needs_you + cards.in_progress + cards.queued + cards.done;
	return (
		<Card
			className={styles.card}
			title={name}
			attention={cards.needs_you > 0}
			aside={
				<PageLink
					to={projectPath(project.id)}
					className={styles.arrow}
					label={`Open ${name}`}
				>
					<ArrowUpRightIcon size={18} />
				</PageLink>
			}
			footer={
				<Button
					kind="ghost"
					aria-label={`Disconnect ${name}`}
					onClick={disconnect}
				>
					Disconnect
				</Button>
			}
		>
			<div className={styles.body}>
				<div className={styles.top}>
					<p className={styles.hero}>{project.name}</p>
					<p className={styles.key}>
						<span className={styles.count}>{cards.needs_you}</span>
						<span className={styles.caption}>
							<span>{cards.needs_you === 1 ? "needs you" : "need you"}</span>
							<span>
								of {total} {total === 1 ? "card" : "cards"}
							</span>
						</span>
					</p>
				</div>
				<Rows project={project} total={total} now={now} />
			</div>
		</Card>
	);
}

/** At most three inset rows: what is building and what is queued, the open
 * feature with its cards landed, and the workers with when one was last
 * seen; or, with no card yet, that there is none. */
function Rows({
	project,
	total,
	now,
}: {
	project: Project;
	total: number;
	now: number;
}) {
	const { cards, feature, workers } = project.summary;
	const building = cards.in_progress + cards.queued > 0;
	return (
		<ul className={styles.glance}>
			{total === 0 ? (
				<Row label="Cards" title="None yet" />
			) : building ? (
				<Row
					label="Building"
					title={
						cards.in_progress > 0
							? `${cards.in_progress} in progress`
							: "Nothing in progress"
					}
					end={
						cards.queued > 0 ? (
							<span className={styles.meta}>{cards.queued} queued</span>
						) : null
					}
				/>
			) : null}
			{feature === null ? null : (
				<Row
					label="Feature"
					title={feature.title}
					end={
						<span className={styles.meta}>
							{feature.landed} / {feature.total} landed
						</span>
					}
				/>
			)}
			{workers.count === 0 || workers.last_seen_at === null ? null : (
				<WorkerRow
					count={workers.count}
					lastSeen={Date.parse(workers.last_seen_at)}
					now={now}
				/>
			)}
		</ul>
	);
}

/** The workers' row: how many, and when the one seen last was seen, Running
 * while that is within QUIET_MS, as the project's page says it. */
function WorkerRow({
	count,
	lastSeen,
	now,
}: {
	count: number;
	lastSeen: number;
	now: number;
}) {
	const ago = Math.max(0, now - lastSeen);
	const quiet = ago >= QUIET_MS;
	const workers = `${count} ${count === 1 ? "worker" : "workers"}`;
	return (
		<Row
			label="Worker"
			title={`${workers} · ${quiet ? "not seen for" : "seen"} ${duration(ago)}${quiet ? "" : " ago"}`}
			end={
				quiet ? (
					<Status tone="idle">Not seen</Status>
				) : (
					<Status tone="running">Running</Status>
				)
			}
		/>
	);
}

/** An inset row: a mono label, its title and, on the right, what ends it. */
function Row({
	label,
	title,
	end,
}: {
	label: string;
	title: string;
	end?: React.ReactNode;
}) {
	return (
		<li className={styles.row}>
			<span className={styles.label}>{label}</span>
			<span className={styles.title}>{title}</span>
			{end ? <span className={styles.end}>{end}</span> : null}
		</li>
	);
}

type ChooserProps = {
	load: () => Promise<Connectable>;
	connect: (installationId: number, repositoryId: number) => Promise<void>;
	close: () => void;
};

/** The repositories the GitHub App reaches for the owner, a table per
 * account, each connected one marked rather than offered; or, with the app
 * installed nowhere they can reach, where to install it. */
function Chooser({ load, connect, close }: ChooserProps) {
	const [connectable, setConnectable] = useState<Connectable>();
	const [error, setError] = useState<string | null>(null);
	const [connecting, setConnecting] = useState(false);

	useEffect(() => {
		let current = true;
		load().then(
			(answer) => {
				if (current) setConnectable(answer);
			},
			() => {
				if (current) setError(NOT_READ);
			},
		);
		return () => {
			current = false;
		};
	}, [load]);

	const choose = async (installation: Installation, repository: Repository) => {
		setConnecting(true);
		try {
			await connect(installation.id, repository.id);
		} catch {
			setError(NOT_CONNECTED);
			setConnecting(false);
		}
	};

	return (
		<section className={styles.chooser} aria-labelledby="choose-repository">
			<div className={styles.chooserHead}>
				<h2 id="choose-repository" className={styles.chooserTitle}>
					Choose a repository
				</h2>
				<Button kind="ghost" onClick={close}>
					Cancel
				</Button>
			</div>
			{error === null ? null : (
				<p className={styles.error} role="alert">
					{error}
				</p>
			)}
			{connectable === undefined ? null : connectable.installations.length ===
				0 ? (
				<EmptyState
					title={NOT_INSTALLED}
					detail="Install it on the account that holds the repository, then connect it here."
					action={
						<a className={styles.install} href={connectable.install_url}>
							Install it on GitHub
						</a>
					}
				/>
			) : (
				connectable.installations.map((installation) => (
					<Table
						key={installation.id}
						className={`${styles.account} ${styles.rows}`}
						label={installation.account}
						rows={installation.repositories}
						rowKey={(repository) => String(repository.id)}
						columns={[
							{
								// The account names the group, seen as well as heard.
								key: "repository",
								header: installation.account,
								cell: (repository) => (
									<span className={styles.name}>{fullName(repository)}</span>
								),
							},
						]}
						rowAction={(repository) =>
							repository.connected ? (
								<Badge>Connected</Badge>
							) : (
								<Button
									aria-label={`Connect ${fullName(repository)}`}
									disabled={connecting}
									onClick={() => choose(installation, repository)}
								>
									Connect
								</Button>
							)
						}
					/>
				))
			)}
		</section>
	);
}
