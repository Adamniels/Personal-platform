import { EmptyState, Tab, TabPanel, Tabs } from "@platform/ui";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";
import { navigate, usePathname } from "./address";
import { createClient } from "./api/client";
import type { components } from "./api/schema";
import styles from "./app.module.css";
import { CodeHostBanner } from "./banner";
import { type Project, Projects, type ProjectsApi } from "./projects";
import { GitHubLink } from "./projects-link";
import { SECTIONS, sectionAt } from "./sections";
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
	/** How what waits is read and refreshed, the service's by default; a test
	 * passes its own. */
	waiting?: WaitingApi;
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

/** What waits, through Dev flow's API. */
const waitingApi: WaitingApi = {
	loadWaiting: () => client.get("/waiting"),
	refresh: async (id) => {
		await client.send("POST", `/projects/${encodeURIComponent(id)}/refresh`);
	},
};

/** Dev flow's page, with React Query for what it reads. */
export function App(props: AppProps = {}) {
	// One per page, so a test's page starts with nothing cached. A failure is
	// shown at once rather than after three retries, as review's page does.
	const [queries] = useState(
		() => new QueryClient({ defaultOptions: { queries: { retry: false } } }),
	);
	return (
		<QueryClientProvider client={queries}>
			<Frame {...props} />
		</QueryClientProvider>
	);
}

/** Dev flow's frame: the fake mode's banner, the page's heading, a tab per
 * section, tied to the address, and the section the address names: what
 * waits, with its count on its tab, and the Projects tab with the GitHub link
 * above its projects. */
function Frame({
	loadStatus = readStatus,
	projects = projectsApi,
	waiting = waitingApi,
	now = Date.now,
}: AppProps) {
	const current = sectionAt(usePathname());
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
