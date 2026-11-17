import { EmptyState, Tab, TabPanel, Tabs, ToastProvider } from "@platform/ui";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import type { ActionsApi } from "./actions";
import { navigate, usePathname } from "./address";
import { createClient } from "./api/client";
import type { components } from "./api/schema";
import styles from "./app.module.css";
import { CodeHostBanner } from "./banner";
import { type ProjectApi, ProjectPage } from "./project";
import { type Project, Projects, type ProjectsApi } from "./projects";
import { GitHubLink } from "./projects-link";
import { projectAt, SECTIONS, type Section, sectionAt } from "./sections";
import { loadStatus as readStatus, type Status, useStatus } from "./status";
import { useWaiting, Waiting, type WaitingApi } from "./waiting";

const client = createClient();

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
	/** How what waits is read and refreshed, the service's by default; a test
	 * passes its own. */
	waiting?: WaitingApi;
	/** How an item is merged and sent back, the service's by default; a test
	 * passes its own. */
	actions?: ActionsApi;
	/** The time, in milliseconds since the epoch, the page says how old the
	 * picture is by; a test passes its own. */
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
	disconnect: async (id) => {
		await client.send("DELETE", `/projects/${encodeURIComponent(id)}`);
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
	makeKey: async (id) =>
		(
			await client.send<components["schemas"]["WorkerKey"]>(
				"POST",
				`/projects/${encodeURIComponent(id)}/worker-key`,
			)
		).key,
};

/** What waits, through Dev flow's API. */
const waitingApi: WaitingApi = {
	loadWaiting: () => client.get("/waiting"),
	refresh: async (id) => {
		await client.send("POST", `/projects/${encodeURIComponent(id)}/refresh`);
	},
};

/** Merging and sending back, through Dev flow's API, as the caller. */
const actionsApi: ActionsApi = {
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
 * waits, with its count on its tab, and the Projects tab with the GitHub link
 * above its projects, or, at a project's address, that project's page. */
function Frame({
	loadStatus = readStatus,
	projects = projectsApi,
	project = projectApi,
	waiting = waitingApi,
	actions = actionsApi,
	now = Date.now,
}: AppProps) {
	const pathname = usePathname();
	// A project's page is under the Projects tab, which stays selected there.
	const projectId = projectAt(pathname);
	const current = projectId
		? SECTIONS.find((section) => section.key === "projects")
		: sectionAt(pathname);
	useCrumb(current);
	const [status, reload] = useStatus(loadStatus);
	const listed = useWaiting(waiting.loadWaiting);
	// The answer's count, on its tab once it is more than none, as the nav's
	// dot shows only when something waits.
	const waitingCount = listed.data?.count || undefined;
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
						count={section.key === "waiting" ? waitingCount : undefined}
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
