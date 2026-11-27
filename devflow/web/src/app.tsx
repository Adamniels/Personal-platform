import { EmptyState, Tab, TabPanel, Tabs, ToastProvider } from "@platform/ui";
import {
	QueryClient,
	QueryClientProvider,
	useQueryClient,
} from "@tanstack/react-query";
import { useEffect, useState } from "react";
import type { ActionsApi } from "./actions";
import { navigate, usePathname } from "./address";
import { ApiError, createClient } from "./api/client";
import type { components } from "./api/schema";
import styles from "./app.module.css";
import { CodeHostBanner } from "./banner";
import { type CardApi, CardPage } from "./card";
import {
	ClaudeLogin,
	type ClaudeLoginApi,
	type ClaudeLoginStatus,
} from "./claude-login";
import { openEvents } from "./events";
import { type FeatureApi, FeaturePage } from "./feature";
import type { FeatureMergeApi } from "./feature-merge";
import { PAUSED, PausedStrip, type PausesApi, usePaused } from "./paused";
import { type ProjectApi, ProjectPage } from "./project";
import { type Project, Projects, type ProjectsApi } from "./projects";
import { GitHubLink } from "./projects-link";
import type { RunAgainApi } from "./run-again";
import type { LoadRunLog } from "./run-log";
import { RUNNING, Running, type RunningApi, useRunning } from "./running";
import {
	cardAt,
	featureAt,
	projectAt,
	projectPath,
	SECTIONS,
	type Section,
	sectionAt,
	settingsAt,
} from "./sections";
import {
	type ProjectSettings,
	SettingsPage,
	type SettingsPageApi,
} from "./settings";
import { loadStatus as readStatus, type Status, useStatus } from "./status";
import { type Period, Usage, type UsageApi } from "./usage";
import { useWaiting, Waiting, type WaitingApi } from "./waiting";

const client = createClient();

export type { PausesApi };

export type AppProps = {
	/** How the status is read, the service's by default; a test passes its
	 * own. */
	loadStatus?: () => Promise<Status>;
	/** How projects are read and changed, the service's by default; a test
	 * passes its own. */
	projects?: ProjectsApi;
	/** How a project's page reads its workers and makes its key, the
	 * service's by default; a test passes its own. */
	project?: ProjectApi;
	/** How the Claude login is read, given and removed, the service's by
	 * default; a test passes its own. */
	claudeLogin?: ClaudeLoginApi;
	/** How what waits is read and refreshed, the service's by default; a test
	 * passes its own. */
	waiting?: WaitingApi;
	/** How an item is merged and sent back, the service's by default; a test
	 * passes its own. */
	actions?: ActionsApi;
	/** How a project's settings page reads and saves its settings and
	 * values and disconnects it, the service's by default; a test passes its
	 * own. */
	settings?: SettingsPageApi;
	/** How a card's page reads its card, the service's by default; a test
	 * passes its own. */
	card?: CardApi;
	/** How a feature's page reads its feature and answers a decision, the
	 * service's by default; a test passes its own. */
	feature?: FeatureApi;
	/** How Running now reads its list, stops a run and opens its streams,
	 * the service's by default; a test passes its own. */
	running?: RunningApi;
	/** How Usage and history reads what was used, the service's by default;
	 * a test passes its own. */
	usage?: UsageApi;
	/** How whether everything is paused is read and changed, the service's
	 * by default; a test passes its own. */
	pauses?: PausesApi;
	/** The time, in milliseconds since the epoch, the page says how old the
	 * picture is by, and counts a run's time to; a test passes its own. */
	now?: () => number;
};

/** Projects through Dev flow's API. */
const projectsApi: ProjectsApi = {
	loadProjects: async () => (await client.get("/projects")).projects,
	loadRepositories: () => client.get("/github/repositories"),
	connect: async (installationId, repositoryId) => {
		await client.send<Project>("POST", "/projects", {
			installation_id: installationId,
			repository_id: repositoryId,
		});
	},
};

/** A project's page, through Dev flow's API. */
const projectApi: ProjectApi = {
	loadProjects: projectsApi.loadProjects,
	// The schema names the path with its parameter; the call fills it in.
	loadWorkers: (id) =>
		client.get(
			`/projects/${encodeURIComponent(id)}/workers` as "/projects/{id}/workers",
		),
	makeKey: (id) =>
		client.send<components["schemas"]["WorkerKey"]>(
			"POST",
			`/projects/${encodeURIComponent(id)}/worker-key`,
		),
	pauseProject: async (id, paused) => {
		await client.send("PUT", `/projects/${encodeURIComponent(id)}/paused`, {
			paused,
		});
	},
	stopNow: async (id) =>
		(
			await client.send<components["schemas"]["StopNowAnswer"]>(
				"POST",
				`/projects/${encodeURIComponent(id)}/stop-now`,
			)
		).stopped,
	loadSettings: (id) =>
		client.get(
			`/projects/${encodeURIComponent(id)}/settings` as "/projects/{id}/settings",
		),
	loadCards: (id) =>
		client.get(
			`/projects/${encodeURIComponent(id)}/cards` as "/projects/{id}/cards",
		),
	loadSetup: (id) =>
		client.get(
			`/projects/${encodeURIComponent(id)}/setup` as "/projects/{id}/setup",
		),
	checkAgain: async (id) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(id)}/setup/check-again`,
		);
	},
	mergeSetup: async (id, head) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(id)}/setup/merge`,
			{ head_commit: head },
		);
	},
	runSetupAgain: async (id, comment) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(id)}/setup/run-again`,
			{ comment },
		);
	},
	sendSetupBack: async (id, comment) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(id)}/setup/send-back`,
			{ comment },
		);
	},
	saveSettings: (id, settings) =>
		client.send<ProjectSettings>(
			"PUT",
			`/projects/${encodeURIComponent(id)}/settings`,
			settings,
		),
	loadValues: (id) =>
		client.get(
			`/projects/${encodeURIComponent(id)}/values` as "/projects/{id}/values",
		),
	giveValue: async (id, name, value) => {
		await client.send(
			"PUT",
			`/projects/${encodeURIComponent(id)}/values/${encodeURIComponent(name)}`,
			{ value },
		);
	},
	removeValue: async (id, name) => {
		await client.send(
			"DELETE",
			`/projects/${encodeURIComponent(id)}/values/${encodeURIComponent(name)}`,
		);
	},
};

/** A project's settings page, through Dev flow's API. */
const settingsApi: SettingsPageApi = {
	loadProjects: projectsApi.loadProjects,
	loadSettings: projectApi.loadSettings,
	saveSettings: projectApi.saveSettings,
	loadValues: projectApi.loadValues,
	giveValue: projectApi.giveValue,
	removeValue: projectApi.removeValue,
	disconnect: async (id) => {
		await client.send("DELETE", `/projects/${encodeURIComponent(id)}`);
	},
};

/** A page of a finished run's log, through Dev flow's API, as a card's page
 * and a feature's read it. */
const loadRunLog: LoadRunLog = (project, run, after) =>
	client.get(
		`/projects/${encodeURIComponent(project)}/runs/${encodeURIComponent(run)}/log?after=${after}` as "/projects/{id}/runs/{run}/log",
	);

/** A card's story, through Dev flow's API; a card the caller does not have
 * answers 404, which the page shows as none. */
const cardApi: CardApi = {
	loadRunLog,
	loadCard: async (project, number) => {
		try {
			return await client.get(
				`/projects/${encodeURIComponent(project)}/cards/${number}` as "/projects/{id}/cards/{number}",
			);
		} catch (error) {
			if (error instanceof ApiError && error.problem.status === 404)
				return null;
			throw error;
		}
	},
};

/** A feature's merge, as the caller, from its page and from Waiting for you. */
const featureMergeApi: FeatureMergeApi = {
	loadFeatureMerge: (project, number) =>
		client.get(
			`/projects/${encodeURIComponent(project)}/features/${number}/merge` as "/projects/{id}/features/{number}/merge",
		),
	mergeFeature: async (project, number, headCommit) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(project)}/features/${number}/merge`,
			{ head_commit: headCommit },
		);
	},
};

/** Running again what the owner stopped, and dropping a card he stopped,
 * as the caller. */
const runAgainApi: RunAgainApi = {
	runCardAgain: async (projectId, number) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/cards/${number}/run-again`,
		);
	},
	dropCard: async (projectId, number) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/cards/${number}/drop`,
		);
	},
	runFeatureAgain: async (projectId, number) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/features/${number}/run-again`,
		);
	},
};

/** A feature's story and its answers, through Dev flow's API; a feature the
 * caller does not have answers 404, which the page shows as none. */
const featureApi: FeatureApi = {
	...featureMergeApi,
	loadRunLog,
	runFeatureAgain: runAgainApi.runFeatureAgain,
	loadFeature: async (project, number) => {
		try {
			return await client.get(
				`/projects/${encodeURIComponent(project)}/features/${number}` as "/projects/{id}/features/{number}",
			);
		} catch (error) {
			if (error instanceof ApiError && error.problem.status === 404)
				return null;
			throw error;
		}
	},
	answerFine: (project, number, run, position) =>
		client.send<components["schemas"]["DecisionAnswer"]>(
			"POST",
			`/projects/${encodeURIComponent(project)}/features/${number}/decisions/${encodeURIComponent(run)}/${position}/fine`,
		),
	sendBack: async (project, number, comment) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(project)}/features/${number}/send-back`,
			{ comment },
		);
	},
	sendBackCard: async (project, card, comment) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(project)}/cards/${card}/send-back`,
			{ comment },
		);
	},
	sendBackDecision: async (project, number, run, position, comment) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(project)}/features/${number}/decisions/${encodeURIComponent(run)}/${position}/send-back`,
			{ comment },
		);
	},
};

/** The Claude login, through Dev flow's API. The token is sent, never
 * read back. */
const claudeLoginApi: ClaudeLoginApi = {
	loadLogin: () => client.get("/claude-login"),
	saveLogin: (token) =>
		client.send<ClaudeLoginStatus>("PUT", "/claude-login", { token }),
	removeLogin: async () => {
		await client.send("DELETE", "/claude-login");
	},
};

/** What waits, through Dev flow's API. */
const waitingApi: WaitingApi = {
	loadWaiting: () => client.get("/waiting"),
	refresh: async (id) => {
		await client.send("POST", `/projects/${encodeURIComponent(id)}/refresh`);
	},
};

/** Whether everything is paused, through Dev flow's API. */
const pausesApi: PausesApi = {
	loadPaused: () => client.get("/paused"),
	pauseAll: async (paused) => {
		await client.send("PUT", "/paused", { paused });
	},
};

/** Usage and history, through Dev flow's API. */
const usageApi: UsageApi = {
	loadUsage: (period: Period) =>
		client.get(`/usage?period=${period}` as "/usage"),
};

/** Running now, through Dev flow's API and its streams. */
const runningApi: RunningApi = {
	loadRunning: () => client.get("/running"),
	stopRun: async (projectId, runId) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/runs/${encodeURIComponent(runId)}/stop`,
		);
	},
	openEvents,
};

/** Merging and sending back, through Dev flow's API, as the caller. */
const actionsApi: ActionsApi = {
	...featureMergeApi,
	...runAgainApi,
	merge: async (id, headCommit) => {
		await client.send("POST", `/waiting/${encodeURIComponent(id)}/merge`, {
			head_commit: headCommit,
		});
	},
	sendBack: async (id, comment) => {
		await client.send("POST", `/waiting/${encodeURIComponent(id)}/send-back`, {
			comment,
		});
	},
	resume: async (projectId, commit) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/resume`,
			{ commit },
		);
	},
	mergeCard: async (projectId, number, headCommit) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/cards/${number}/merge`,
			{ head_commit: headCommit },
		);
	},
	sendBackCard: async (projectId, number, comment) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/cards/${number}/send-back`,
			{ comment },
		);
	},
	sendBackFeature: async (projectId, number, comment) => {
		await client.send(
			"POST",
			`/projects/${encodeURIComponent(projectId)}/features/${number}/send-back`,
			{ comment },
		);
	},
	answerCard: async (cardId, readingId, answers) => {
		await client.send("POST", `/cards/${encodeURIComponent(cardId)}/answers`, {
			reading_id: readingId,
			answers,
		});
	},
};

/** Dev flow's page, with React Query for what it reads and the toasts its
 * actions report in. */
export function App(props: AppProps = {}) {
	// One per page, so a test's page starts with nothing cached. A failure is
	// shown at once rather than after three retries, as review's page does.
	const [queries] = useState(
		() => new QueryClient({ defaultOptions: { queries: { retry: false } } }),
	);
	return (
		<QueryClientProvider client={queries}>
			<ToastProvider>
				<Frame {...props} />
			</ToastProvider>
		</QueryClientProvider>
	);
}

/** Dev flow's frame: the fake mode's banner, the page's heading, a tab per
 * section, tied to the address, and the section the address names: what
 * waits, with its count on its tab, the runs under way, with the busy
 * workers on its tab, and the Projects tab with Connections,
 * the GitHub link and the Claude login one line each, above its projects, or,
 * at a project's address, that project's page. */
function Frame({
	loadStatus = readStatus,
	projects = projectsApi,
	project = projectApi,
	settings = settingsApi,
	claudeLogin = claudeLoginApi,
	waiting = waitingApi,
	actions = actionsApi,
	card = cardApi,
	feature = featureApi,
	running = runningApi,
	usage = usageApi,
	pauses = pausesApi,
	now = Date.now,
}: AppProps) {
	const pathname = usePathname();
	// A project's page, and its settings page, a card's page and a feature's
	// page under it, are under the Projects tab, which stays selected there.
	const projectId = projectAt(pathname);
	const settingsOf = settingsAt(pathname);
	const cardOf = cardAt(pathname);
	const featureOf = featureAt(pathname);
	const current =
		projectId || settingsOf || cardOf || featureOf
			? SECTIONS.find((section) => section.key === "projects")
			: sectionAt(pathname);
	useCrumb(current);
	const [status, reload] = useStatus(loadStatus);
	const listed = useWaiting(waiting.loadWaiting);
	// The answer's count, on its tab once it is more than none, as the nav's
	// dot shows only when something waits.
	const waitingCount = listed.data?.count || undefined;
	// The busy workers, on Running now's tab once there is one.
	const runs = useRunning(running.loadRunning);
	const busy = runs.data?.busy_workers || undefined;
	// Whether everything is paused: the strip under the heading on every tab,
	// and the switch on Running now, read once for both.
	const queries = useQueryClient();
	const paused = usePaused(pauses.loadPaused);
	const pauseAll = async (value: boolean) => {
		await pauses.pauseAll(value);
		await Promise.all([
			queries.invalidateQueries({ queryKey: PAUSED }),
			queries.invalidateQueries({ queryKey: RUNNING }),
		]);
	};
	// Read once: the return from the code host sends the browser here with
	// ?link=failed when it did not link.
	const [failed] = useState(
		() => new URLSearchParams(window.location.search).get("link") === "failed",
	);

	return (
		<main className="page">
			<CodeHostBanner status={status} />
			<header className={styles.header}>
				<h1 className={styles.title}>Dev flow</h1>
				<p className={styles.lede}>
					What your agents built, and what waits for you.
				</p>
				<PausedStrip paused={paused.data} continueAll={() => pauseAll(false)} />
			</header>
			<Tabs
				label="Dev flow sections"
				value={current?.key ?? ""}
				onChange={(key) => {
					const next = SECTIONS.find((section) => section.key === key);
					if (next) navigate(next.path);
				}}
			>
				{SECTIONS.map((section) => (
					<Tab
						key={section.key}
						value={section.key}
						count={
							section.key === "waiting"
								? waitingCount
								: section.key === "running"
									? busy
									: undefined
						}
					>
						{section.label}
					</Tab>
				))}
				{SECTIONS.map((section) => (
					<TabPanel key={section.key} value={section.key}>
						{section !== current ? null : section.key === "waiting" ? (
							<Waiting
								list={listed.data}
								failed={listed.isError}
								refresh={waiting.refresh}
								reload={listed.refetch}
								now={now}
								link={status?.github.state}
								actions={actions}
							/>
						) : section.key === "running" ? (
							<Running
								list={runs.data}
								failed={runs.isError}
								now={now}
								paused={paused.data?.paused}
								pauseAll={pauseAll}
								{...running}
							/>
						) : section.key === "usage" ? (
							<Usage {...usage} />
						) : section.key === "projects" && settingsOf ? (
							<SettingsPage
								key={settingsOf}
								id={settingsOf}
								back={() => navigate(projectPath(settingsOf))}
								disconnected={() => navigate(section.path)}
								{...settings}
							/>
						) : section.key === "projects" && cardOf ? (
							<CardPage
								key={`${cardOf.project}/${cardOf.number}`}
								project={cardOf.project}
								number={cardOf.number}
								backPath={projectPath(cardOf.project)}
								back={() => navigate(projectPath(cardOf.project))}
								{...card}
							/>
						) : section.key === "projects" && featureOf ? (
							<FeaturePage
								key={`${featureOf.project}/${featureOf.number}`}
								project={featureOf.project}
								number={featureOf.number}
								backPath={projectPath(featureOf.project)}
								back={() => navigate(projectPath(featureOf.project))}
								link={status?.github.state}
								{...feature}
							/>
						) : section.key === "projects" && projectId ? (
							<ProjectPage
								key={projectId}
								id={projectId}
								now={now}
								back={() => navigate(section.path)}
								{...project}
							/>
						) : section.key === "projects" ? (
							<>
								<section
									className={styles.connections}
									aria-label="Connections"
								>
									<GitHubLink
										github={status?.github}
										failed={failed}
										start={startLink}
										unlink={async () => {
											await client.send("DELETE", "/github/link");
											await reload();
										}}
										go={(url) => window.location.assign(url)}
									/>
									<ClaudeLogin {...claudeLogin} />
								</section>
								<Projects
									linked={status?.github.state === "linked"}
									{...projects}
								/>
							</>
						) : null}
					</TabPanel>
				))}
			</Tabs>
			{current ? null : <NotFound />}
		</main>
	);
}

/** Names the tab in the top bar: the crumb of the page's `<platform-nav>`,
 * which sits outside the React root in index.html, is "Dev flow / " and the
 * tab's name. Where the address names no tab the attribute is removed, so the
 * bar shows the name of its link, "Dev flow". See "Navigation" in
 * High-level/docs/platform-architecture.md. */
function useCrumb(current: Section | undefined) {
	const label = current?.label;
	useEffect(() => {
		const nav = document.querySelector("platform-nav");
		if (!nav) return;
		if (label) nav.setAttribute("crumb", `Dev flow / ${label}`);
		else nav.removeAttribute("crumb");
	}, [label]);
}

/** Asks Dev flow to start a link, answering the code host's address. */
async function startLink(): Promise<string> {
	const started = await client.send<components["schemas"]["LinkStarted"]>(
		"POST",
		"/github/link",
	);
	return started.authorize_url;
}

function NotFound() {
	return (
		<EmptyState
			className={styles.missing}
			title="There is no such section"
			detail="The address names no part of Dev flow."
			action={
				<a className={styles.link} href="/devflow">
					Go to Waiting for you
				</a>
			}
		/>
	);
}
