// A project's setup on its page. While it is setting up, in place of its cards and features, the
// "Setting it up" card: a steps bar, and the six steps of setting it up as a step list, each done,
// current or to come by its own flag, the first not done the one open, with its content as part 6
// built it: the setup run with its command, its state and Merge; the settings its setup run
// proposed with "Accept"; the values step, where the owner gives the values its tests need; and
// the newest check, with its output, its pictures of the project's address, and "Check again" and
// "Send back with a comment" when it failed. Above the cards of a registered project whose newer
// version failed its check, the "Check failed" card, with that check alone. See "Setting up a
// project" in devflow/README.md.
import {
	Button,
	Card,
	Status,
	StepList,
	StepListItem,
	TextArea,
} from "@platform/ui";
import { type FormEvent, type ReactNode, useState } from "react";
import { describe } from "./actions";
import { navigate, plainClick } from "./address";
import type { GetAnswer } from "./api/client";
import { StepsBar } from "./cards";
import { CommentBox } from "./comment-box";
import { Command } from "./project";
import styles from "./setup.module.css";
import { type ValuesApi, ValuesSection } from "./values";

export type ProjectSetup = GetAnswer<"/projects/{id}/setup">;
export type SetupCheck = NonNullable<ProjectSetup["check"]>;
export type SetupProposal = NonNullable<ProjectSetup["proposal"]>;
export type SetupProjectItem = GetAnswer<"/waiting">["setup_projects"][number];

/** How the page reads a project's setup and asks for another check, Dev
 * flow's API in the page and a test's own in a test. */
export type SetupApi = {
	loadSetup: (id: string) => Promise<ProjectSetup>;
	/** Asks for another check of the version whose newest check failed. */
	checkAgain: (id: string) => Promise<void>;
	/** Merges the setup's pull request as the owner, at the commit shown. */
	mergeSetup: (id: string, head: string) => Promise<void>;
	/** Runs a setup that stopped before it had a pull request again, with the
	 * owner's comment. */
	runSetupAgain: (id: string, comment: string) => Promise<void>;
	/** Sends the failed check back with the owner's comment, which starts a
	 * new setup run. */
	sendSetupBack: (id: string, comment: string) => Promise<void>;
};

export type SetupRunInfo = NonNullable<ProjectSetup["setup"]>;
export type SetupRunItem = GetAnswer<"/waiting">["setup_runs"][number];

/** Each step's title, by the route's word. */
export const STEP_TITLES: Record<string, string> = {
	setup: "The setup run",
	recipe: "The recipe on the default branch",
	settings: "The settings",
	values: "The values its tests need",
	worker: "A worker on the recipe",
	check: "The check at registration",
};

/** The one line of what happened, by step, once it is done. The setup
 * run's names its pull request when it had one. */
export const STEP_DONE: Record<string, string> = {
	setup: ".devflow/ is valid on the default branch.",
	recipe: "Dev flow has read the recipe from the default branch.",
	settings: "The settings are saved.",
	values: "Every value its tests need is given.",
	worker: "A worker built from the recipe has said hello.",
	check: "A check of the recipe passed.",
};

/** The one line of what comes there, by step, while it is still to come. */
export const STEP_TO_COME: Record<string, string> = {
	setup:
		"An agent writes .devflow/ on a branch and opens a pull request with it.",
	recipe: "Dev flow reads the recipe once .devflow/ is on the default branch.",
	settings:
		"You accept the settings the setup run proposes, or save the settings once.",
	values: "You give the values its tests need, if its setup run lists any.",
	worker: "A worker is built from the recipe, and says hello.",
	check:
		"A worker runs the project's check on a clean copy. Once it passes, cards run.",
};

/** What the current step says when it holds nothing to do, by step. */
export const STEP_NOW: Record<string, string> = {
	recipe:
		"The recipe is not on the default branch yet: Dev flow reads it there on its next read of the project.",
	settings: STEP_TO_COME.settings as string,
	worker:
		"Start a worker on the machine that runs your workers: Start a worker, under Workers, has its commands.",
	check: "The next worker on the recipe runs its check.",
};
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
export const RUN_SETUP_AGAIN = "Comment to run again";
export const COMMENT_RUNS_AGAIN = "A comment runs it again.";
export const COMMENT_ON_PULL =
	"Comment on its pull request, and it runs again.";
export const SENT_BACK_RUN =
	"You sent the failed check back: an agent writes .devflow/ again on a new branch and opens a pull request with it. Start its worker on the machine that runs your workers with this command, after saving the project's key below:";
export const SETTINGS_PROPOSED =
	"The setup run proposes these settings. Accept them, or change them under Edit settings and save them there.";
export const SETTINGS_SAVE_ONCE =
	"Look over the settings under Edit settings, and save them once. Until you do, this project hands out no reading or build.";
export const NOT_ACCEPTED = "The proposal was not saved.";
export const SEND_BACK = "Send back with a comment";
export const SEND_BACK_LABEL = "What should the setup do differently?";

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

/** What each preset a setup proposes means, from what it fills in: see
 * PRESET_FILLS in settings.tsx. */
export const PRESET_MEANINGS: Record<string, string> = {
	fully_automatic: "Questions off; no pull request waits for you.",
	guided:
		"Questions off; a pull request waits for you when it is marked, touches a listed path, or has a decision for you.",
	only_i_merge: "Questions on; every pull request waits for you.",
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

/** Where each step is, and whether the current one is the owner's. */
export type SetupProgress = {
	steps: {
		step: string;
		state: "done" | "current" | "to-come";
		/** The step is the current one and waits for the owner. */
		mine: boolean;
	}[];
	done: number;
	total: number;
	/** The current step waits for the owner. */
	mine: boolean;
};

/** Whether the setup step is still under way: its flag is not done, or a
 * setup run is, such as one sent back from a failed check, which leaves the
 * valid .devflow/ on the default branch, and so the flag, as it was. */
const setupDue = (setup: ProjectSetup) =>
	setup.steps.some((step) => step.step === "setup" && !step.done) ||
	(setup.setup !== null && setup.setup.state !== "done");

/** Whether the current step waits for the owner: the settings, the values,
 * a setup run that waits for him or stopped, or a failed check; never the
 * setup run while it works, the recipe or the worker. */
const stepIsMine = (step: string, setup: ProjectSetup): boolean => {
	switch (step) {
		case "settings":
		case "values":
			return true;
		case "setup": {
			const run = setup.setup;
			return (
				run !== null &&
				!run.running &&
				(run.state === "waiting_for_owner" || run.state === "stopped")
			);
		}
		case "check":
			return failedCheck(setup);
		default:
			return false;
	}
};

/** The newest check failed, and is the owner's to check again or send
 * back: no other is asked for or running. */
const failedCheck = (setup: ProjectSetup) =>
	setup.check !== null &&
	!setup.check.passed &&
	!setup.check.again_asked &&
	!setup.checking;

/** Where each step is: done by its own flag, since they need not finish in
 * order, the setup step while no setup run is under way; the first not done
 * in the route's order is the current one; the rest are to come. */
export const setupProgress = (setup: ProjectSetup): SetupProgress => {
	const done = (step: { step: string; done: boolean }) =>
		step.step === "setup" ? !setupDue(setup) : step.done;
	const current = setup.steps.findIndex((step) => !done(step));
	const steps = setup.steps.map((step, at) => {
		const state: "done" | "current" | "to-come" = done(step)
			? "done"
			: at === current
				? "current"
				: "to-come";
		return {
			step: step.step,
			state,
			mine: state === "current" && stepIsMine(step.step, setup),
		};
	});
	return {
		steps,
		done: steps.filter((step) => step.state === "done").length,
		total: steps.length,
		mine: steps.some((step) => step.mine),
	};
};

/** The header's summary line while the project is setting up: how far, and
 * whether the next step is the owner's. */
export const settingUpLine = ({ done, total, mine }: SetupProgress) =>
	`No card runs until setup is done: ${done} of ${total} steps${mine ? ", and the next one is yours" : ""}`;

/** The project's setup on its page: while it is setting up, the "Setting it
 * up" card with its steps; once registered, a newer version's failed check
 * as the "Check failed" card. */
export function SetupPanel({
	id,
	setup,
	checkAgain,
	mergeSetup,
	runSetupAgain,
	sendSetupBack,
	acceptProposal,
	editSettings,
	editing,
	copy,
	reload,
	values,
}: {
	id: string;
	setup: ProjectSetup;
	/** Reads and gives the project's values, for the values step. */
	values: ValuesApi;
	checkAgain: SetupApi["checkAgain"];
	mergeSetup: SetupApi["mergeSetup"];
	runSetupAgain: SetupApi["runSetupAgain"];
	sendSetupBack: SetupApi["sendSetupBack"];
	/** Saves the proposed preset's settings and paths through the settings
	 * route, which does the settings step. */
	acceptProposal: (proposal: SetupProposal) => Promise<void>;
	/** Opens the settings panel, and closes it again. */
	editSettings: () => void;
	/** Whether the settings panel is open. */
	editing: boolean;
	/** Copies the setup command to the clipboard. */
	copy: (text: string) => Promise<void>;
	/** Reads the setup again, after another check was asked for. */
	reload: () => Promise<unknown>;
}) {
	const check = (
		<CheckStep
			id={id}
			setup={setup}
			checkAgain={checkAgain}
			sendSetupBack={sendSetupBack}
			reload={reload}
		/>
	);

	if (setup.state !== "setting_up") {
		return (
			<Card title="Check failed" className={styles.card} attention>
				<div className={styles.body}>
					<p className={styles.line}>{NEWER_FAILED}</p>
					{check}
				</div>
			</Card>
		);
	}

	const progress = setupProgress(setup);
	const content = (step: string): ReactNode => {
		switch (step) {
			case "setup":
				return (
					<SetupRunSection
						id={id}
						setup={setup}
						proposalShown={setup.settings_waits && setup.proposal !== null}
						mergeSetup={mergeSetup}
						runSetupAgain={runSetupAgain}
						copy={copy}
						reload={reload}
					/>
				);
			case "recipe":
				return (
					<div className={styles.run}>
						<p className={styles.line}>{STEP_NOW.recipe}</p>
						{setup.problem === null ? null : (
							<p className={styles.muted}>{setup.problem}</p>
						)}
					</div>
				);
			case "settings":
				// The settings step waits, and is offered, only after the
				// setup's pull request, as the route decides: a save before then
				// does not do it.
				return setup.settings_waits ? (
					<SettingsSection
						proposal={setup.proposal}
						acceptProposal={acceptProposal}
						editSettings={editSettings}
						editing={editing}
					/>
				) : (
					<p className={styles.line}>{STEP_NOW.settings}</p>
				);
			case "values":
				return (
					<ValuesSection
						id={id}
						loadValues={values.loadValues}
						giveValue={values.giveValue}
						removeValue={values.removeValue}
						onChanged={reload}
					/>
				);
			case "check":
				return check;
			default:
				return STEP_NOW[step] ?? null;
		}
	};

	return (
		<Card
			title="Setting it up"
			className={styles.card}
			attention={progress.mine}
			aside={
				<StepsBar
					className={styles.bar}
					label={`${progress.done} of ${progress.total} steps done`}
					steps={progress.steps.map(({ state }) =>
						state === "to-come" ? "to-do" : state,
					)}
				/>
			}
		>
			<StepList aria-label="Setup steps" className={styles.steps}>
				{progress.steps.map(({ step, state, mine }) => (
					<StepListItem
						key={step}
						state={state}
						title={STEP_TITLES[step] ?? step}
						aside={
							mine ? <Status tone="needs-you">Needs you</Status> : undefined
						}
					>
						{state === "current" ? (
							content(step)
						) : state === "done" ? (
							<DoneLine step={step} setup={setup} />
						) : (
							(STEP_TO_COME[step] ?? null)
						)}
					</StepListItem>
				))}
			</StepList>
		</Card>
	);
}

/** A done step's one line of what happened: the setup run's names its pull
 * request, linked, when it had one. */
function DoneLine({ step, setup }: { step: string; setup: ProjectSetup }) {
	const run = setup.setup;
	if (
		step === "setup" &&
		run !== null &&
		run.state === "done" &&
		run.pull_request_url !== null
	) {
		return (
			<>
				An agent wrote .devflow/ and its{" "}
				<a className={styles.link} href={run.pull_request_url}>
					pull request #{run.pull_request_number}
				</a>{" "}
				merged.
			</>
		);
	}
	return <>{STEP_DONE[step] ?? null}</>;
}

/** The check step's content: whether a check is running, the newest check
 * with its output and pictures, and, when it failed, "Check again", the
 * step's primary, and "Send back with a comment"; or, before one ran, who
 * runs it. */
function CheckStep({
	id,
	setup,
	checkAgain,
	sendSetupBack,
	reload,
}: {
	id: string;
	setup: ProjectSetup;
	checkAgain: SetupApi["checkAgain"];
	sendSetupBack: SetupApi["sendSetupBack"];
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
		<div className={styles.run}>
			{setup.checking ? (
				<p className={styles.line}>
					<Status tone="running">Running</Status> {CHECK_RUNNING}
				</p>
			) : check === null ? (
				<p className={styles.line}>{STEP_NOW.check}</p>
			) : null}
			{error === null ? null : (
				<p className={styles.error} role="alert">
					{error}
				</p>
			)}
			{check === null ? null : (
				<CheckResult
					id={id}
					check={check}
					checkAgain={
						canAsk ? (
							<Button kind="primary" disabled={asking} onClick={ask}>
								Check again
							</Button>
						) : null
					}
					sendBack={
						setup.can_send_back
							? async (comment) => {
									await sendSetupBack(id, comment);
									await reload();
								}
							: null
					}
				/>
			)}
		</div>
	);
}

/** The settings step while it waits: the preset the setup run proposed as
 * a section heading, what it means, why and the paths, with "Accept" as the
 * step's primary and "Edit settings"; or, without a proposal, to save the
 * settings once, with "Edit settings". */
function SettingsSection({
	proposal,
	acceptProposal,
	editSettings,
	editing,
}: {
	proposal: SetupProposal | null;
	acceptProposal: (proposal: SetupProposal) => Promise<void>;
	editSettings: () => void;
	editing: boolean;
}) {
	const [accepting, setAccepting] = useState(false);
	const [error, setError] = useState<string | null>(null);

	const accept = async (shown: SetupProposal) => {
		setAccepting(true);
		try {
			await acceptProposal(shown);
			setError(null);
		} catch (failure) {
			setError(`${NOT_ACCEPTED} ${describe(failure)}`);
		}
		setAccepting(false);
	};

	const edit = (
		<Button kind="secondary" aria-expanded={editing} onClick={editSettings}>
			Edit settings
		</Button>
	);

	return (
		<section className={styles.run} aria-label="The settings">
			{proposal === null ? (
				<>
					<p className={styles.line}>{SETTINGS_SAVE_ONCE}</p>
					<div className={styles.actions}>{edit}</div>
				</>
			) : (
				<div className={styles.proposal}>
					<p className={styles.line}>{SETTINGS_PROPOSED}</p>
					<h3 className={styles.preset}>
						{PRESETS[proposal.preset] ?? proposal.preset}
					</h3>
					<p className={styles.line}>
						{PRESET_MEANINGS[proposal.preset] ?? null}
					</p>
					<p className={styles.line}>{proposal.why}</p>
					<p className={styles.label}>Paths that wait for you</p>
					{proposal.paths.length === 0 ? (
						<p className={styles.muted}>None.</p>
					) : (
						<ul className={styles.paths} aria-label="Paths that wait for you">
							{proposal.paths.map((path) => (
								<li key={path}>{path}</li>
							))}
						</ul>
					)}
					{error === null ? null : (
						<p className={styles.error} role="alert">
							{error}
						</p>
					)}
					<div className={styles.actions}>
						<Button
							kind="primary"
							disabled={accepting}
							onClick={() => accept(proposal)}
						>
							Accept
						</Button>
						{edit}
					</div>
				</div>
			)}
		</section>
	);
}

/** The setup run's part of the step: why it is due, the command that starts
 * its worker, and its newest run, where it is, its pull request, its review,
 * what it proposes, the values its tests need and what it could not settle,
 * Merge when it waits for the owner, and, when it stopped before it had a pull
 * request, the comment that runs it again. */
function SetupRunSection({
	id,
	setup,
	proposalShown,
	mergeSetup,
	runSetupAgain,
	copy,
	reload,
}: {
	id: string;
	setup: ProjectSetup;
	/** Whether the settings step shows what it proposes, so it is not
	 * shown here twice. */
	proposalShown: boolean;
	mergeSetup: SetupApi["mergeSetup"];
	runSetupAgain: SetupApi["runSetupAgain"];
	copy: (text: string) => Promise<void>;
	reload: () => Promise<unknown>;
}) {
	const [merging, setMerging] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const run = setup.setup;
	const shown = run === null ? null : (SETUP_STATES[run.state] ?? null);
	const report = run?.report ?? null;
	const [writing, setWriting] = useState(false);
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const stopped = run !== null && run.state === "stopped" && !run.running;

	const send = async (event: FormEvent) => {
		event.preventDefault();
		if (text.trim() === "") return;
		setSending(true);
		try {
			await runSetupAgain(id, text);
			setError(null);
			setWriting(false);
			setText("");
			await reload();
		} catch (failure) {
			setError(describe(failure));
		}
		setSending(false);
	};

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
					<p className={styles.line}>
						{run?.sent_back ? SENT_BACK_RUN : NO_DEVFLOW}
					</p>
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
							{proposalShown ? null : (
								<>
									<p className={styles.label}>What it proposes</p>
									<p className={styles.line}>
										<span className={styles.kind}>
											{PRESETS[report.proposed.preset] ??
												report.proposed.preset}
										</span>
										<span>{report.proposed.why}</span>
									</p>
								</>
							)}
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
					{stopped && run.pull_request_number !== null ? (
						<p className={styles.muted}>{COMMENT_ON_PULL}</p>
					) : null}
					{stopped && run.pull_request_number === null ? (
						writing ? (
							<form className={styles.comment} onSubmit={send}>
								<TextArea
									label={COMMENT_RUNS_AGAIN}
									value={text}
									rows={4}
									onChange={(event) => setText(event.target.value)}
								/>
								<div className={styles.actions}>
									<Button
										type="submit"
										disabled={sending || text.trim() === ""}
									>
										Send
									</Button>
									<Button
										kind="ghost"
										onClick={() => {
											setWriting(false);
											setText("");
										}}
									>
										Cancel
									</Button>
								</div>
							</form>
						) : (
							<div>
								<Button onClick={() => setWriting(true)}>
									{RUN_SETUP_AGAIN}
								</Button>
							</div>
						)
					) : null}
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
 * its output, its pictures, and, when it failed and may be, Send back with a
 * comment. */
function CheckResult({
	id,
	check,
	checkAgain,
	sendBack,
}: {
	id: string;
	check: SetupCheck;
	/** The "Check again" button, or null when it is not offered. */
	checkAgain: ReactNode;
	/** Sends it back with the owner's comment; null when it is not offered. */
	sendBack: ((comment: string) => Promise<void>) | null;
}) {
	const [writing, setWriting] = useState(false);
	const [error, setError] = useState<string | null>(null);

	const send = async (comment: string) => {
		try {
			await sendBack?.(comment);
			setError(null);
			setWriting(false);
			return true;
		} catch (failure) {
			setError(describe(failure));
			return false;
		}
	};

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
			{error === null ? null : (
				<p className={styles.error} role="alert">
					{error}
				</p>
			)}
			{sendBack !== null && writing ? (
				<CommentBox
					label={SEND_BACK_LABEL}
					send={send}
					cancel={() => setWriting(false)}
				/>
			) : checkAgain === null && sendBack === null ? null : (
				<div className={styles.actions}>
					{checkAgain}
					{sendBack === null ? null : (
						<Button kind="secondary" onClick={() => setWriting(true)}>
							{SEND_BACK}
						</Button>
					)}
				</div>
			)}
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
						? "It starts no card's work until its setup passes its check."
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
