// The projects on the Projects tab, under the GitHub link: the connected repositories, connecting
// one from those the GitHub App reaches for the owner, and disconnecting one behind a
// confirmation. A project has no page of its own yet. See "The page" and "Connecting a
// repository" in devflow/README.md.
import { Badge, Button, Dialog, EmptyState, Table } from "@platform/ui";
import { useCallback, useEffect, useState } from "react";
import type { GetAnswer } from "./api/client";
import styles from "./projects.module.css";

export type Project = GetAnswer<"/projects">["projects"][number];
export type Connectable = GetAnswer<"/github/repositories">;
type Installation = Connectable["installations"][number];
type Repository = Installation["repositories"][number];

export const RUNNER_LINE = "Read the way this repository's runner writes.";
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
};

const fullName = (item: { owner: string; name: string }) =>
	`${item.owner}/${item.name}`;

/** "Connect a repository", the list to choose from when it is open, and
 * the projects, or an empty state when there are none. Nothing of the list
 * before it is read. */
export function Projects({
	linked,
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

	return (
		<>
			<div className={styles.actions}>
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
				<Table
					label="Projects"
					rows={projects}
					rowKey={(project) => project.id}
					columns={[
						{
							key: "repository",
							header: "Repository",
							cell: (project) => (
								<div className={styles.repository}>
									<span className={styles.name}>{fullName(project)}</span>
									<span className={styles.how}>{RUNNER_LINE}</span>
								</div>
							),
						},
					]}
					rowAction={(project) => (
						<Button
							kind="ghost"
							aria-label={`Disconnect ${fullName(project)}`}
							onClick={() => setLeaving(project)}
						>
							Disconnect
						</Button>
					)}
				/>
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
		</>
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
						className={styles.account}
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
