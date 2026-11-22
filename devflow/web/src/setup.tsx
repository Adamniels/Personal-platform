// A project's setup on its page: while it is setting up, in place of its cards and features, the
// steps of setting it up, each done or not, and its newest check, with the check's output, its
// pictures of the project's address, and "Check again" when it failed; and above the cards of a
// registered project whose newer version failed its check. See "Setting up a project" in
// devflow/README.md.
import { Button, Card, Status } from "@platform/ui";
import { useState } from "react";
import { navigate, plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import { Command } from "./project";
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
	/** Merges the setup's pull request as the owner, at the commit shown. */
	mergeSetup: (id: string, head: string) => Promise<void>;
};

export type SetupRunInfo = NonNullable<ProjectSetup["setup"]>;
export type SetupRunItem = GetAnswer<"/waiting">["setup_runs"][number];

/** Each step as the page names it, by the route's word. */
export const STEP_NAMES: Record<string, string> = {
	setup: "A valid .devflow/ on the default branch",
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
export const NO_DEVFLOW =
	"An agent writes this project's .devflow/ on a branch and opens a pull request with it. Start its worker on the machine that runs your workers with this command, after saving the project's key below:";
export const NOT_MERGED =
	"It was not merged. Look at it on GitHub, or try again.";

/** Each setup state as its step shows it, with its status's tone. */
const SETUP_STATES: Record<
	string,
	{ label: string; tone: "needs-you" | "done" | "idle" | "failed" | "running" }
> = {
	queued: { label: "Queued", tone: "idle" },
	checking: { label: "Opening its pull request", tone: "running" },
	reviewing: { label: "Being reviewed", tone: "running" },
	fixing: { label: "Being fixed", tone: "running" },
	waiting_for_owner: { label: "Waits for you", tone: "needs-you" },
	stopped: { label: "Stopped", tone: "failed" },
	done: { label: "Merged", tone: "done" },
};

/** Each kind of what a setup could not settle, as the page names it. */
export const UNSETTLED_KINDS: Record<string, string> = {
	real_secret: "Needs a real secret",
	containers_in_tests: "Its tests start containers",
	macos_needed: "Needs macOS",
	other: "Other",
};

/** Each preset a setup proposes, as the settings panel names it. */
const PRESETS: Record<string, string> = {
	fully_automatic: "Fully automatic",
	guided: "Guided",
	only_i_merge: "Only I merge",
};

/** A sentence, with its full stop. */
const sentence = (text: string) =>
	/[.!?]$/.test(text.trim()) ? text.trim() : `${text.trim()}.`;

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
	mergeSetup,
	copy,
	reload,
}: {
	id: string;
	setup: ProjectSetup;
	checkAgain: SetupApi["checkAgain"];
	mergeSetup: SetupApi["mergeSetup"];
	/** Copies the setup command to the clipboard. */
	copy: (text: string) => Promise<void>;
	/** Reads the setup again, after another check was asked for. */
	reload: () => Promise<unknown>;
}) {
	const setupDue =
		setup.steps.some((step) => step.step === "setup" && !step.done) ||
		(setup.setup !== null && setup.setup.state !== "done");
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
				{setupDue ? (
					<SetupRunSection
						id={id}
						setup={setup}
						mergeSetup={mergeSetup}
						copy={copy}
						reload={reload}
					/>
				) : null}
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

/** The setup run's part of the step: why it is due, the command that starts
 * its worker, and its newest run, where it is, its pull request, its review,
 * what it proposes, the values its tests need and what it could not settle,
 * and Merge when it waits for the owner. */
function SetupRunSection({
	id,
	setup,
	mergeSetup,
	copy,
	reload,
}: {
	id: string;
	setup: ProjectSetup;
	mergeSetup: SetupApi["mergeSetup"];
	copy: (text: string) => Promise<void>;
	reload: () => Promise<unknown>;
}) {
	const [merging, setMerging] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const run = setup.setup;
	const shown = run === null ? null : (SETUP_STATES[run.state] ?? null);
	const report = run?.report ?? null;

	const merge = async (head: string) => {
		setMerging(true);
		try {
			await mergeSetup(id, head);
			setError(null);
			await reload();
		} catch {
			setError(NOT_MERGED);
		}
		setMerging(false);
	};

	return (
		<section className={styles.run} aria-label="The setup run">
			{setup.problem === null ? null : (
				<p className={styles.muted}>{setup.problem}</p>
			)}
			{run === null || run.state !== "done" ? (
				<>
					<p className={styles.line}>{NO_DEVFLOW}</p>
					<Command
						label="Start the setup worker"
						copyLabel="Copy the command"
						text={setup.setup_command}
						copy={copy}
					/>
				</>
			) : null}
			{run === null || shown === null ? null : (
				<>
					<p className={styles.result}>
						<Status tone={run.running ? "running" : shown.tone}>
							{run.running ? "Running" : shown.label}
						</Status>
						{run.reason === null ? null : <span>{sentence(run.reason)}</span>}
					</p>
					{run.pull_request_url === null ? null : (
						<p className={styles.line}>
							<a className={styles.open} href={run.pull_request_url}>
								Pull request #{run.pull_request_number}
							</a>
							<span className={styles.muted}>
								on <span className={styles.mono}>{run.branch}</span>
							</span>
						</p>
					)}
					{run.review === null ? null : (
						<p className={styles.line}>
							<Status
								tone={run.review.verdict === "approve" ? "done" : "failed"}
							>
								{run.review.verdict === "approve" ? "Approved" : "Changes"}
							</Status>
							<span>{run.review.summary}</span>
						</p>
					)}
					{report === null ? null : (
						<>
							<p className={styles.line}>{report.summary}</p>
							<p className={styles.label}>What it could not settle</p>
							{report.unsettled.length === 0 ? (
								<p className={styles.muted}>Nothing.</p>
							) : (
								<ul
									className={styles.list}
									aria-label="What it could not settle"
								>
									{report.unsettled.map((each) => (
										<li
											key={`${each.kind}-${each.detail}`}
											className={styles.unsettled}
										>
											<span className={styles.kind}>
												{UNSETTLED_KINDS[each.kind] ?? each.kind}
											</span>
											<span>{each.detail}</span>
										</li>
									))}
								</ul>
							)}
							<p className={styles.label}>What it proposes</p>
							<p className={styles.line}>
								<span className={styles.kind}>
									{PRESETS[report.proposed.preset] ?? report.proposed.preset}
								</span>
								<span>{report.proposed.why}</span>
							</p>
							{report.variables.length === 0 ? null : (
								<>
									<p className={styles.label}>The values its tests need</p>
									<ul
										className={styles.list}
										aria-label="The values its tests need"
									>
										{report.variables.map((variable) => (
											<li key={variable.name} className={styles.unsettled}>
												<span className={styles.mono}>{variable.name}</span>
												<span>{variable.why}</span>
											</li>
										))}
									</ul>
								</>
							)}
						</>
					)}
					{error === null ? null : (
						<p className={styles.error} role="alert">
							{error}
						</p>
					)}
					{run.state === "waiting_for_owner" && run.head_commit !== null ? (
						<div>
							<Button
								kind="primary"
								disabled={merging}
								onClick={() => merge(run.head_commit as string)}
							>
								Merge
							</Button>
						</div>
					) : null}
				</>
			)}
		</section>
	);
}

/** What "Waiting for you" says of a project whose setup run waits for the
 * owner or stopped. */
export const setupRunTitle = (project: SetupRunItem) =>
	project.state === "stopped"
		? `${project.project_name}'s setup stopped`
		: `${project.project_name}'s setup waits for you`;

/** A project whose setup run waits for the owner or stopped, under "Setup"
 * on "Waiting for you"; its step, with Merge, is on its page. */
export function WaitingSetupRun({ project }: { project: SetupRunItem }) {
	const page = `/devflow/projects/${encodeURIComponent(project.project_id)}`;
	const stopped = project.state === "stopped";
	return (
		<Card
			title={setupRunTitle(project)}
			aside={
				stopped ? (
					<Status tone="failed">Stopped</Status>
				) : (
					<Status tone="needs-you">Needs you</Status>
				)
			}
			className={styles.item}
		>
			<div className={styles.body}>
				<p className={styles.muted}>
					{project.owner}/{project.project_name}
				</p>
				{project.reason === null ? null : (
					<p className={styles.line}>{sentence(project.reason)}</p>
				)}
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
						{stopped
							? "See why on the project's page"
							: "Merge it from the project's page"}
					</a>
				</p>
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
