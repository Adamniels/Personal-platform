// A project's setup on its page: while it is setting up, in place of its cards and features, the
// steps of setting it up, each done or not, and its newest check, with the check's output, its
// pictures of the project's address, and "Check again" when it failed; and above the cards of a
// registered project whose newer version failed its check. See "Setting up a project" in
// devflow/README.md.
import { Button, Card, Status } from "@platform/ui";
import { useState } from "react";
import { navigate, plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import styles from "./setup.module.css";

export type ProjectSetup = GetAnswer<"/projects/{id}/setup">;
export type SetupCheck = NonNullable<ProjectSetup["check"]>;
export type SetupProjectItem = GetAnswer<"/waiting">["setup_projects"][number];

/** How the page reads a project's setup and asks for another check, Dev
 * flow's API in the page and a test's own in a test. */
export type SetupApi = {
	loadSetup: (id: string) => Promise<ProjectSetup>;
	/** Asks for another check of the version whose newest check failed. */
	checkAgain: (id: string) => Promise<void>;
};

/** Each step as the page names it, by the route's word. */
export const STEP_NAMES: Record<string, string> = {
	recipe: "The recipe on the default branch",
	worker: "A worker on the recipe",
	check: "The check passed",
};

export const SETTING_UP =
	"Dev flow hands out no card until this project's setup has passed its check.";
export const NEWER_FAILED =
	"The newest version of this project's setup failed its check, so its workers on that version get no work.";
export const CHECK_RUNNING = "The check is running.";
export const AGAIN_ASKED =
	"Another check was asked for: the next worker on this version runs it.";
export const NOT_ASKED = "Another check was not asked for. Try again.";

/** Whether the page shows the setup panel: while the project is setting up,
 * and when its newest check failed with no other asked for. */
export const setupShows = (setup: ProjectSetup) =>
	setup.state === "setting_up" ||
	(setup.check !== null && !setup.check.passed && !setup.check.again_asked);

/** The address of one of a check's pictures. */
export const pictureUrl = (id: string, check: string, name: string) =>
	`/devflow/api/projects/${encodeURIComponent(id)}/setup/checks/${encodeURIComponent(check)}/pictures/${encodeURIComponent(name)}`;

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

/** The Setup panel: what setting up is for, the steps, and the newest
 * check. */
export function SetupPanel({
	id,
	setup,
	checkAgain,
	reload,
}: {
	id: string;
	setup: ProjectSetup;
	checkAgain: SetupApi["checkAgain"];
	/** Reads the setup again, after another check was asked for. */
	reload: () => Promise<unknown>;
}) {
	const [asking, setAsking] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const { check } = setup;
	const canAsk = check !== null && !check.passed && !check.again_asked;

	const ask = async () => {
		setAsking(true);
		try {
			await checkAgain(id);
			setError(null);
			await reload();
		} catch {
			setError(NOT_ASKED);
		}
		setAsking(false);
	};

	return (
		<Card
			title="Setting up"
			className={styles.card}
			aside={
				setup.state === "setting_up" ? (
					<Status tone="needs-you">Setting up</Status>
				) : (
					<Status tone="failed">Check failed</Status>
				)
			}
			footer={
				canAsk ? (
					<Button kind="secondary" disabled={asking} onClick={ask}>
						Check again
					</Button>
				) : undefined
			}
		>
			<div className={styles.body}>
				<p className={styles.line}>
					{setup.state === "setting_up" ? SETTING_UP : NEWER_FAILED}
				</p>
				<ol className={styles.steps} aria-label="Setup steps">
					{setup.steps.map((step) => (
						<li key={step.step} className={styles.step}>
							<Status tone={step.done ? "done" : "idle"}>
								{step.done ? "Done" : "To do"}
							</Status>
							<span>{STEP_NAMES[step.step] ?? step.step}</span>
						</li>
					))}
				</ol>
				{setup.checking ? (
					<p className={styles.line}>
						<Status tone="running">Running</Status> {CHECK_RUNNING}
					</p>
				) : null}
				{error === null ? null : (
					<p className={styles.error} role="alert">
						{error}
					</p>
				)}
				{check === null ? null : <CheckResult id={id} check={check} />}
			</div>
		</Card>
	);
}

/** The newest check: whether it passed, of which version and commit, when,
 * its output, and its pictures. */
function CheckResult({ id, check }: { id: string; check: SetupCheck }) {
	return (
		<section className={styles.check} aria-label="The newest check">
			<p className={styles.result}>
				{check.passed ? (
					<Status tone="done">Passed</Status>
				) : (
					<Status tone="failed">Failed</Status>
				)}
				<span>
					The check of version{" "}
					<span className={styles.mono}>{check.version.slice(0, 7)}</span> at{" "}
					<span className={styles.mono}>{check.commit.slice(0, 7)}</span>{" "}
					{check.passed ? "passed" : `exited with ${check.exit_code}`},{" "}
					<time className={styles.mono} dateTime={check.checked_at}>
						{DATE_TIME.format(new Date(check.checked_at))}
					</time>
					.
				</span>
			</p>
			{check.again_asked ? <p className={styles.muted}>{AGAIN_ASKED}</p> : null}
			<p className={styles.label}>Its output, the last 200 lines</p>
			<pre className={styles.output}>
				<code>{check.output}</code>
			</pre>
			{check.pictures.length > 0 ? (
				<ul className={styles.pictures} aria-label="The project's address">
					{check.pictures.map((name) => (
						<li key={name}>
							<a href={pictureUrl(id, check.id, name)}>
								<img
									className={styles.picture}
									src={pictureUrl(id, check.id, name)}
									alt={`The project's address at ${name.endsWith("-phone") ? "a phone's" : "the desktop's"} size`}
								/>
							</a>
						</li>
					))}
				</ul>
			) : check.pictures_note ? (
				<p className={styles.muted}>{check.pictures_note}</p>
			) : null}
		</section>
	);
}

/** What "Waiting for you" says of a project whose newest check of its
 * setup failed, which sends the owner to its page, where its output and
 * "Check again" are. */
export const setupFailedTitle = (project: SetupProjectItem) =>
	`${project.project_name}'s setup failed its check`;

/** A project whose newest check of its setup failed, under "Setup" on
 * "Waiting for you". */
export function WaitingSetup({ project }: { project: SetupProjectItem }) {
	const page = `/devflow/projects/${encodeURIComponent(project.project_id)}`;
	return (
		<Card
			title={setupFailedTitle(project)}
			aside={<Status tone="failed">Check failed</Status>}
			className={styles.item}
		>
			<div className={styles.body}>
				<p className={styles.muted}>
					{project.owner}/{project.project_name}
				</p>
				<p className={styles.line}>
					{project.state === "setting_up"
						? "It hands out no card until its setup passes its check."
						: "Its workers on the newest version of its setup get no work until it passes its check."}{" "}
					The check of version{" "}
					<span className={styles.mono}>{project.version.slice(0, 7)}</span>{" "}
					exited with {project.exit_code},{" "}
					<time className={styles.mono} dateTime={project.checked_at}>
						{DATE_TIME.format(new Date(project.checked_at))}
					</time>
					.
				</p>
				<p className={styles.line}>
					<a
						className={styles.open}
						href={page}
						onClick={(event) => {
							if (plainClick(event)) {
								event.preventDefault();
								navigate(page);
							}
						}}
					>
						See its output on the project's page
					</a>
				</p>
			</div>
		</Card>
	);
}
