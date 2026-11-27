// A card's page, /devflow/projects/{project}/cards/{number}, under the Projects tab, opened from the
// project's Cards panel and from its feature's page: the card's whole story so far, read from GET
// /devflow/api/projects/{id}/cards/{number}, what Dev flow stored and never the code host. Its
// header, a Needs you banner linking to its item on Waiting for you when it needs the owner, what it
// was read as, what the agent did from its latest report, how it got here, its runs, reviews and fix
// rounds as one timeline, oldest first, and at a glance beside them. It holds no action.
// Every text came from the code host, a worker or an agent and is shown as text. See "The pages"
// and "A card's page" in devflow/README.md.
import {
	EmptyState,
	NeedsYouBanner,
	Card as Panel,
	Status,
	type StatusTone,
	Timeline,
	TimelineEntry,
} from "@platform/ui";
import { useEffect, useId, useState } from "react";
import { navigate, plainClick } from "./address";
import type { components } from "./api/schema";
import styles from "./card.module.css";
import { StepsBar, stateWord } from "./cards";
import { Inline } from "./format";
import { FinishedLog, KEPT_90_DAYS, type LoadRunLog } from "./run-log";
import { cardScreenshot, Screenshots } from "./screenshots";
import { featurePath } from "./sections";
import { sentence } from "./sentence";
import { itemPath, linkable } from "./waiting";

export type CardStory = components["schemas"]["CardStoryView"];
export type Run = CardStory["runs"][number];
type Report = NonNullable<CardStory["report"]>;
type Review = CardStory["reviews"][number];
type Round = CardStory["rounds"][number];
type CardReading = NonNullable<CardStory["reading"]>;

export const NOT_READ = "The card could not be read. Try again.";

/** How the page reads a card, Dev flow's API in the page and a test's own
 * in a test. Null when the caller has no such card. */
export type CardApi = {
	loadCard: (project: string, number: number) => Promise<CardStory | null>;
	/** Reads a page of a finished run's log. */
	loadRunLog: LoadRunLog;
};

export type CardPageProps = CardApi & {
	project: string;
	number: number;
	/** Goes back to the project's page. */
	back: () => void;
	/** The project page's address, for the way back. */
	backPath: string;
};

/** A run's result as a word and a tone; one the page does not know yet is
 * shown as it is named. */
const RESULTS: Record<string, { word: string; tone: StatusTone }> = {
	succeeded: { word: "Succeeded", tone: "done" },
	stopped: { word: "The agent stopped", tone: "failed" },
	check_failed: { word: "Check failed", tone: "failed" },
	failed_in_card: { word: "Failed", tone: "failed" },
	failed_outside_card: { word: "Failed", tone: "failed" },
	report_invalid: { word: "Report did not match", tone: "failed" },
	lost: { word: "Lost", tone: "failed" },
};

/** A run's kind as the pages name it. */
export const KINDS: Record<string, string> = {
	build: "Build",
	review: "Review",
	fix: "Fix",
	conflict: "Conflict",
	feature_review: "Review of the feature",
	feature_fix: "Fix of the feature",
	read: "Reading",
};

/** What a reading answered for the card, as a word and a tone; one the page
 * does not know yet is shown as it is named. */
const OUTCOMES: Record<string, { word: string; tone: StatusTone }> = {
	ready: { word: "Ready to build", tone: "done" },
	questions: { word: "Built on the recommendations", tone: "idle" },
	too_thin: { word: "Too thin to build", tone: "failed" },
};

/** What an earlier reading answered, as a word and a tone, by its outcome. */
const EARLIER: Record<string, { word: string; tone: StatusTone }> = {
	ready: { word: "Ready", tone: "done" },
	questions: { word: "Questions", tone: "idle" },
	too_thin: { word: "Too thin", tone: "failed" },
};

/** What the review of a fix's commit said of it, after "the review of it". */
const AFTER: Record<string, string> = {
	approve: "approved it",
	changes: "asked for changes",
};

/** A verdict as a word and a tone; one the page does not know yet is shown
 * as it is named. */
const VERDICTS: Record<string, { word: string; tone: StatusTone }> = {
	approve: { word: "Approved", tone: "done" },
	changes: { word: "Changes asked for", tone: "failed" },
};

/** The reviewer's verdict as the pages say it. */
export function verdictWord(verdict: string): {
	word: string;
	tone: StatusTone;
} {
	return VERDICTS[verdict] ?? { word: verdict, tone: "idle" };
}

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

const COUNT = new Intl.NumberFormat("en-GB");

/** How long a run took, to the second below an hour and to the minute
 * after. */
export function took(ms: number): string {
	const seconds = Math.round(ms / 1000);
	if (seconds < 60) return `${seconds} s`;
	const minutes = Math.floor(seconds / 60);
	if (minutes < 60) return `${minutes} min ${seconds % 60} s`;
	return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** The card states that need the owner, each with the key of its item on
 * Waiting for you, where every action on it is. */
const ITEM_KEYS: Record<string, string> = {
	waiting_for_owner: "card",
	stopped: "card",
	refused: "refused",
	needs_answers: "questions",
};

/** The page: the header, the Needs you banner when the card needs the owner,
 * then the main column, what it was read as, what the agent did and how it
 * got here, beside the side column, at a glance and its screenshots, which
 * stacks under it on phone; or a not found state. Nothing before the card is
 * read. The page holds no action: all are on Waiting for you. */
export function CardPage({
	project,
	number,
	back,
	backPath,
	loadCard,
	loadRunLog,
}: CardPageProps) {
	const [story, setStory] = useState<CardStory | null>();
	const [failed, setFailed] = useState(false);

	useEffect(() => {
		let current = true;
		loadCard(project, number).then(
			(answer) => {
				if (current) setStory(answer);
			},
			() => {
				if (current) setFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [project, number, loadCard]);

	const backLink = (
		<a
			className={styles.back}
			href={backPath}
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					back();
				}
			}}
		>
			Back to the project
		</a>
	);

	if (failed) {
		return (
			<p className={styles.error} role="alert">
				{NOT_READ}
			</p>
		);
	}
	if (story === undefined) return null;
	if (story === null) {
		return (
			<EmptyState
				title="There is no such card"
				detail="It may have left its project, or the project was disconnected."
				action={backLink}
			/>
		);
	}

	const { word, tone } = stateWord(story.state);
	const caption = `${story.repository} · Card #${story.number} · ${
		story.feature === null ? "No feature" : `Feature #${story.feature.number}`
	}`.toUpperCase();
	return (
		<>
			<header className={styles.header}>
				{backLink}
				<div className={styles.captionRow}>
					<Status tone={tone}>{word}</Status>
					<p className={styles.caption}>{caption}</p>
				</div>
				<h2 className={`${styles.name} ${styles.title}`}>{story.title}</h2>
				<p className={styles.links}>
					<Link url={story.web_url} label={`Issue #${story.number}`} />
					{story.pull_request === null ? null : (
						<Link
							url={story.pull_request.url}
							label={`Pull request #${story.pull_request.number}`}
						/>
					)}
					{story.feature === null ? null : (
						<a
							className={styles.link}
							href={featurePath(project, story.feature.number)}
							onClick={(event) => {
								if (plainClick(event) && story.feature) {
									event.preventDefault();
									navigate(featurePath(project, story.feature.number));
								}
							}}
						>
							Feature #{story.feature.number}: {story.feature.title}
						</a>
					)}
				</p>
			</header>
			<Banner story={story} project={project} />
			{/* The main column, then the side column, which stacks under it on
			    phone. */}
			<div className={styles.columns}>
				<div className={styles.main}>
					{story.reading === null ? null : (
						<Reading
							reading={story.reading}
							earlier={story.earlier_readings}
							state={story.state}
						/>
					)}
					{story.report === null ? null : <WhatItDid report={story.report} />}
					<HowItGotHere story={story} log={{ project, load: loadRunLog }} />
				</div>
				<div className={styles.side}>
					<AtAGlance story={story} />
					{story.screenshots === null ? null : (
						<Panel title="Screenshots">
							<div className={styles.body}>
								<p className={styles.muted}>
									Taken by the worker of{" "}
									<code className={styles.code}>
										{story.screenshots.commit.slice(0, 7)}
									</code>
									, after its check passed.
								</p>
								<Screenshots
									titled={false}
									address={(name) => cardScreenshot(project, number, name)}
									groups={story.screenshots.groups}
									note={story.screenshots.note}
								/>
							</div>
						</Panel>
					)}
				</div>
			</div>
		</>
	);
}

/** "n things", or "1 thing". */
function count(n: number, one: string, many = `${one}s`): string {
	return `${n} ${n === 1 ? one : many}`;
}

/** The Needs you banner, for a card that needs the owner: what it needs, and
 * its one link, to its item on Waiting for you; none in any other state. */
function Banner({ story, project }: { story: CardStory; project: string }) {
	const kind = ITEM_KEYS[story.state];
	if (kind === undefined) return null;
	const { message, detail } = bannerWords(story);
	const to = itemPath(`${kind}/${project}/${story.number}`);
	return (
		<NeedsYouBanner
			data-banner=""
			className={styles.banner}
			message={message}
			detail={detail}
			action={
				<a
					className={styles.primary}
					href={to}
					onClick={(event) => {
						if (plainClick(event)) {
							event.preventDefault();
							navigate(to);
						}
					}}
				>
					Decide in Waiting for you
				</a>
			}
		/>
	);
}

/** What the banner says for each state that needs the owner. */
function bannerWords(story: CardStory): { message: string; detail?: string } {
	switch (story.state) {
		case "waiting_for_owner": {
			const mine =
				story.report?.decisions.filter((decision) => decision.for_owner)
					.length ?? 0;
			return {
				message: "It passed its check and its review, and waits for you.",
				detail:
					mine === 0
						? "Merging lands it; it marked no decision for you."
						: `Merging confirms the ${count(mine, "decision")} it marked for you.`,
			};
		}
		case "stopped":
			return {
				message: "It stopped, and runs again on your comment.",
				detail: story.reason ?? undefined,
			};
		case "refused": {
			const missing = story.reading?.missing ?? [];
			return {
				message: "It is too thin to build.",
				detail:
					missing.length === 0
						? (story.reason ?? undefined)
						: `It lacks: ${missing.join("; ")}.`,
			};
		}
		default: {
			const questions = story.reading?.questions.length ?? 0;
			return {
				message: `It has ${count(questions, "question")} for you before it is built.`,
			};
		}
	}
}

/** What the agent did, from the latest report: its summary, then Tasks and
 * Tests side by side, its Pages, its Decisions, those for the owner first and
 * the rest behind a button, and its Follow ups. An empty list is not shown. */
function WhatItDid({ report }: { report: Report }) {
	const done = report.tasks.filter((task) => task.done).length;
	const passed = report.tests.filter((test) => test.result === "passed").length;
	return (
		<Panel
			title="What the agent did"
			aside={<span className={styles.aside}>latest report</span>}
		>
			<div className={styles.body}>
				{report.summary.trim() === "" ? null : (
					<p className={styles.summary}>
						<Inline text={report.summary} />
					</p>
				)}
				{report.tasks.length === 0 &&
				report.tests.length === 0 &&
				report.pages.length === 0 ? null : (
					<div className={styles.pair}>
						<Section
							label="Tasks"
							count={`${done} of ${report.tasks.length} done`}
							items={report.tasks}
							plain
						>
							{(task) => (
								<span className={styles.task}>
									<span className={task.done ? styles.done : styles.notDone}>
										{task.done ? "Done" : "Not done"}
									</span>
									<Inline text={task.text} />
								</span>
							)}
						</Section>
						<div className={styles.stack}>
							<Section
								label="Tests"
								count={`${passed} passed`}
								items={report.tests}
								plain
							>
								{(test) => (
									<span className={styles.test}>
										<Inline text={test.what} />
										<span
											className={
												test.result === "passed" ? styles.done : styles.notDone
											}
										>
											{test.result}
										</span>
									</span>
								)}
							</Section>
							<Section label="Pages" items={report.pages} plain>
								{(page) => (
									<>
										<code className={styles.code}>{page.path}</code>{" "}
										<span className={styles.mark}>
											{page.looked
												? "Looked at"
												: "The agent did not look at this page"}
										</span>
										{page.why.trim() === "" ? null : (
											<span className={styles.sub}>
												<Inline text={page.why} />
											</span>
										)}
									</>
								)}
							</Section>
						</div>
					</div>
				)}
				<Decisions decisions={report.decisions} />
				<Section label="Follow ups" items={report.follow_ups} plain>
					{(followUp) => <Inline text={followUp} />}
				</Section>
			</div>
		</Panel>
	);
}

/** The report's decisions: those marked for the owner first, each on an inset
 * marked "For you", and the others behind "Show the n it did not need you
 * for". */
function Decisions({ decisions }: { decisions: Report["decisions"] }) {
	const [all, setAll] = useState(false);
	const listId = useId();
	const labelId = useId();
	if (decisions.length === 0) return null;
	const mine = decisions.filter((decision) => decision.for_owner);
	const rest = decisions.filter((decision) => !decision.for_owner);
	const shown = all ? [...mine, ...rest] : mine;
	return (
		<div className={styles.section}>
			<h3 className={styles.label}>
				<span id={labelId}>Decisions</span>{" "}
				<span className={styles.count}>
					{mine.length} for you · {rest.length} more
				</span>
			</h3>
			{shown.length === 0 ? null : (
				<ul id={listId} aria-labelledby={labelId} className={styles.items}>
					{shown.map((decision, index) => (
						// A report's decisions have no id of their own, and the list
						// is read whole.
						// biome-ignore lint/suspicious/noArrayIndexKey: see above
						<li key={index} className={styles.item}>
							{decision.for_owner ? (
								<span className={styles.forYou}>For you</span>
							) : null}{" "}
							<span className={styles.chosen}>
								<Inline text={decision.chosen} />
							</span>
							{decision.alternative.trim() === "" ? null : (
								<span className={styles.sub}>
									Instead of <Inline text={decision.alternative} />
								</span>
							)}
							{decision.why.trim() === "" ? null : (
								<span className={styles.sub}>
									Because <Inline text={decision.why} />
								</span>
							)}
						</li>
					))}
				</ul>
			)}
			{rest.length === 0 ? null : (
				<button
					type="button"
					className={styles.show}
					aria-expanded={all}
					aria-controls={listId}
					onClick={() => setAll((shownAll) => !shownAll)}
				>
					{all
						? `Hide the ${rest.length} it did not need you for`
						: `Show the ${rest.length} it did not need you for`}
				</button>
			)}
		</div>
	);
}

const SHORT = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
	hour: "2-digit",
	minute: "2-digit",
});

/** When, in an entry's mono line. */
function when(at: string): string {
	return SHORT.format(new Date(at));
}

/** One entry of the card's timeline, by when it started or was reviewed. */
/** Its entry carries its key, set when it is built. */
type Moment = { at: string; entry: React.ReactElement };

/** How it got here: every run, review and fix round as one timeline, oldest
 * first, ending in now. A fix run is told as its round, and a review run that
 * kept a verdict as its review; every other run, a read run among them, as
 * itself. */
function HowItGotHere({ story, log }: { story: CardStory; log: LogSource }) {
	const reviews = new Map(
		story.reviews.map((review) => [review.run_id, review]),
	);
	const rounds = new Map(story.rounds.map((round) => [round.run_id, round]));
	const runs = new Map(story.runs.map((run) => [run.id, run]));
	const moments: Moment[] = [];
	for (const run of story.runs) {
		const round = rounds.get(run.id);
		if (round !== undefined) {
			moments.push({
				at: run.started_at,
				entry: (
					<RoundEntry
						key={`round-${run.id}`}
						run={run}
						round={round}
						reviews={story.reviews}
						most={story.most_rounds}
						log={log}
					/>
				),
			});
		} else if (!reviews.has(run.id)) {
			moments.push({
				at: run.started_at,
				entry: <RunEntry key={`run-${run.id}`} run={run} log={log} />,
			});
		}
	}
	for (const review of story.reviews) {
		moments.push({
			at: review.reviewed_at,
			entry: (
				<ReviewEntry
					key={`review-${review.run_id}`}
					review={review}
					run={runs.get(review.run_id)}
					log={log}
				/>
			),
		});
	}
	// Oldest first; the sort is stable, so equal times keep the story's order.
	moments.sort((one, other) => Date.parse(one.at) - Date.parse(other.at));
	const { word, tone } = stateWord(story.state);
	return (
		<Panel
			title="How it got here"
			aside={<span className={styles.aside}>oldest first</span>}
		>
			<div className={styles.body}>
				{story.runs.length === 0 ? (
					<p className={styles.muted}>No run has taken this card yet.</p>
				) : null}
				<Timeline aria-label="How it got here">
					{moments.map((moment) => moment.entry)}
					<TimelineEntry
						key="now"
						time="Now"
						title="Now"
						status={{ tone, word }}
						attention={ITEM_KEYS[story.state] !== undefined}
						detail={story.reason ?? undefined}
					/>
				</Timeline>
			</div>
		</Panel>
	);
}

/** A run's facts on its entry's mono line: when, where it ran, how long it
 * took, what it used and how it logged in. */
function runLine(run: Run): string {
	return [
		when(run.started_at),
		run.worker,
		run.duration_ms === null ? null : `took ${took(run.duration_ms)}`,
		run.input_tokens === null && run.output_tokens === null
			? null
			: `${COUNT.format(run.input_tokens ?? 0)} in / ${COUNT.format(run.output_tokens ?? 0)} out`,
		run.auth_method === null ? null : `logged in with ${run.auth_method}`,
	]
		.filter((part) => part !== null)
		.join(" · ");
}

/** A run's result as a word and a tone, Running while it runs. */
export function resultOf(result: string | null): {
	word: string;
	tone: StatusTone;
} {
	return result === null
		? { word: "Running", tone: "running" }
		: (RESULTS[result] ?? { word: result, tone: "idle" });
}

/** A run as an entry: its kind and result, and for a failure why, where it
 * lay and Claude Code's error output, then its check with the output behind
 * Show output. */
function RunEntry({ run, log }: { run: Run; log: LogSource }) {
	const { word, tone } = resultOf(run.result);
	const title = KINDS[run.kind] ?? run.kind;
	return (
		<TimelineEntry time={runLine(run)} title={title} status={{ tone, word }}>
			<Failure run={run} />
			<CheckLine run={run} />
			<RunLog run={run} title={title} source={log} />
		</TimelineEntry>
	);
}

/** Why a run failed and where it lay, and its error output behind a button
 * when its worker sent any; nothing for a run that did not fail. */
function Failure({ run }: { run: Run }) {
	const [showing, setShowing] = useState(false);
	const id = useId();
	const tail = run.stderr_tail ?? "";
	return (
		<>
			{run.reason === null ? null : (
				<p className={styles.text}>
					{sentence(run.reason)}
					{run.in_card === null
						? null
						: run.in_card
							? LAY.card.in
							: LAY.card.outside}
				</p>
			)}
			{tail.trim() === "" ? null : (
				<>
					<p className={styles.text}>
						<button
							type="button"
							className={styles.show}
							aria-expanded={showing}
							aria-controls={id}
							onClick={() => setShowing((shown) => !shown)}
						>
							{showing
								? "Hide Claude Code's error output"
								: "Show Claude Code's error output"}
						</button>
					</p>
					{showing ? (
						<pre id={id} className={styles.output}>
							{tail}
						</pre>
					) : null}
				</>
			)}
		</>
	);
}

/** A run's check, its exit code and the commit it ran on, with its output
 * behind Show output; nothing when no check ran. */
function CheckLine({ run }: { run: Run }) {
	const [showing, setShowing] = useState(false);
	const id = useId();
	if (run.check === null) return null;
	return (
		<>
			<p className={styles.text}>
				<span>
					Check exit {run.check.exit_code}
					{run.head_commit === null ? null : (
						<>
							{" "}
							on{" "}
							<code className={styles.code}>{run.head_commit.slice(0, 7)}</code>
						</>
					)}
				</span>
				{" · "}
				<button
					type="button"
					className={styles.show}
					aria-expanded={showing}
					aria-controls={id}
					onClick={() => setShowing((shown) => !shown)}
				>
					{showing ? "Hide output" : "Show output"}
				</button>
			</p>
			{showing ? (
				<pre id={id} className={styles.output}>
					{run.check.output}
				</pre>
			) : null}
		</>
	);
}

/** A review as an entry: the commit it is of and its verdict, whether it
 * counts for the head and is on the pull request, the reviewer's summary,
 * its findings, blocking ones first, each on an inset, and its view on each
 * decision marked for the owner. */
function ReviewEntry({
	review,
	run,
	log,
}: {
	review: Review;
	run?: Run;
	log: LogSource;
}) {
	const verdict = verdictWord(review.verdict);
	const findings = [...review.findings].sort(
		(one, other) => Number(other.blocking) - Number(one.blocking),
	);
	const line = [
		when(review.reviewed_at),
		run?.worker ?? null,
		review.current
			? "for the pull request's head"
			: "for an older commit, so it does not count",
		review.posted ? "on the pull request" : "not on the pull request yet",
	]
		.filter((part) => part !== null)
		.join(" · ");
	return (
		<TimelineEntry
			time={line}
			title={
				<>
					Review of{" "}
					<code className={styles.code}>{review.commit.slice(0, 7)}</code>
				</>
			}
			status={verdict}
		>
			<Section level={4} label="Findings" items={findings}>
				{(finding) => (
					<>
						<span className={finding.blocking ? styles.blocking : styles.mark}>
							{finding.blocking ? "Blocking" : "Not blocking"}
						</span>{" "}
						{finding.file.trim() === "" ? null : (
							<>
								<code className={styles.code}>
									{finding.line > 0
										? `${finding.file}:${finding.line}`
										: finding.file}
								</code>{" "}
							</>
						)}
						<Inline text={finding.what} />
						{finding.why.trim() === "" ? null : (
							<span className={styles.sub}>
								Because <Inline text={finding.why} />
							</span>
						)}
					</>
				)}
			</Section>
			{review.summary.trim() === "" ? null : (
				<p className={styles.text}>
					<Inline text={review.summary} />
				</p>
			)}
			<Section level={4} label="For the owner" items={review.for_owner}>
				{(view) => (
					<>
						<Inline text={view.decision} />
						<span className={styles.sub}>
							<Inline text={view.view} />
						</span>
					</>
				)}
			</Section>
			{run === undefined ? null : (
				<RunLog
					run={run}
					title={`Review of ${review.commit.slice(0, 7)}`}
					source={log}
				/>
			)}
		</TimelineEntry>
	);
}

/** A fix round as an entry: its number of the project's rounds, or the
 * owner's comment that started it, and how its fix went; what sent the card
 * back, his words as he wrote them, the developer's replies on an inset, a
 * failure's why and error output, and the commit it left with its check and
 * the review of it. */
function RoundEntry({
	run,
	round,
	reviews,
	most,
	log,
}: {
	run: Run;
	round: Round;
	reviews: Review[];
	most: number;
	log: LogSource;
}) {
	const { word, tone } = resultOf(round.result);
	const asked = reviews.find((review) => review.run_id === round.review_run_id);
	const after = reviews.find(
		(review) => review.run_id === round.review_after_run_id,
	);
	const title =
		round.round === null
			? "Fix, for your comment"
			: `Fix, round ${round.round} of ${most}`;
	return (
		<TimelineEntry time={runLine(run)} title={title} status={{ tone, word }}>
			{round.round === null ? (
				<p className={styles.text}>You sent it back with a comment.</p>
			) : asked === undefined ? null : (
				<p className={styles.text}>
					The review of{" "}
					<code className={styles.code}>{asked.commit.slice(0, 7)}</code> asked
					for changes.
				</p>
			)}
			<Section level={4} label="What you wrote" items={round.comments}>
				{(comment) => <span className={styles.said}>{comment}</span>}
			</Section>
			<Section level={4} label="The developer's replies" items={round.replies}>
				{(reply) => (
					<>
						<span className={styles.chosen}>
							<Inline text={reply.finding} />
						</span>
						<span className={styles.sub}>
							<Inline text={reply.answer} />
						</span>
					</>
				)}
			</Section>
			<Failure run={run} />
			{round.fixed_commit === null ? null : (
				<p className={styles.text}>
					It left{" "}
					<code className={styles.code}>{round.fixed_commit.slice(0, 7)}</code>
					{round.result !== "succeeded"
						? "."
						: after === undefined
							? ", not reviewed yet."
							: `, and the review of it ${AFTER[after.verdict] ?? `said ${after.verdict}`}.`}
				</p>
			)}
			<CheckLine run={run} />
			<RunLog run={run} title={title} source={log} />
		</TimelineEntry>
	);
}

/** Where a run's log is read from: its project, and how. */
export type LogSource = { project: string; load: LoadRunLog };

/** A finished run's "Log", which opens its log under it, the view of
 * "Running now", not live, with "Show more"; for a run whose log was kept
 * its 90 days, that it was. Nothing for a run under way, whose log is on
 * "Running now", nor for one from before logs were kept. */
export function RunLog({
	run,
	title,
	source,
}: {
	run: Run;
	/** What the run is called where it is shown, which names its log. */
	title: string;
	source: LogSource;
}) {
	const [showing, setShowing] = useState(false);
	const id = useId();
	if (run.finished_at === null) return null;
	if (run.log_pruned) return <p className={styles.muted}>{KEPT_90_DAYS}</p>;
	if (!run.has_log) return null;
	return (
		<>
			<p className={styles.text}>
				<button
					type="button"
					className={styles.show}
					aria-expanded={showing}
					aria-controls={id}
					onClick={() => setShowing((shown) => !shown)}
				>
					{showing ? "Hide log" : "Log"}
				</button>
			</p>
			{showing ? (
				<div id={id}>
					<FinishedLog
						project={source.project}
						run={run.id}
						label={`Log of ${title}`}
						load={source.load}
					/>
				</div>
			) : null}
		</>
	);
}

/** At a glance: for a card Dev flow merged, the merge first; then its check,
 * its review, its runs with their total time, the tokens they used, and its
 * fix rounds used of the project's as a steps bar. */
function AtAGlance({ story }: { story: CardStory }) {
	// The runs are newest first; the head's check is the newest build's or
	// fix's that succeeded, as the story reads its head.
	const built = story.runs.find(
		(run) =>
			(run.kind === "build" || run.kind === "fix") &&
			run.result === "succeeded",
	);
	const review = story.reviews.find((each) => each.current) ?? story.reviews[0];
	const total = story.runs.reduce(
		(sum, run) => sum + (run.duration_ms ?? 0),
		0,
	);
	const tokens = story.runs.some(
		(run) => run.input_tokens !== null || run.output_tokens !== null,
	);
	const read = story.runs.reduce(
		(sum, run) => sum + (run.input_tokens ?? 0),
		0,
	);
	const wrote = story.runs.reduce(
		(sum, run) => sum + (run.output_tokens ?? 0),
		0,
	);
	return (
		<Panel title="At a glance">
			<div className={styles.body}>
				{story.merge === null ? null : (
					<div className={styles.merge}>
						<h3 className={styles.label}>The merge</h3>
						<p className={styles.text}>
							Into <code className={styles.code}>{story.merge.base}</code>, by
							Dev flow as the app.
						</p>
						<p className={styles.muted}>
							Merge commit{" "}
							<code className={styles.code}>
								{story.merge.commit.slice(0, 7)}
							</code>{" "}
							of the approved{" "}
							<code className={styles.code}>
								{story.merge.approved.slice(0, 7)}
							</code>
							, on{" "}
							<time dateTime={story.merge.merged_at}>
								{DATE_TIME.format(new Date(story.merge.merged_at))}
							</time>
							.
						</p>
					</div>
				)}
				<dl className={styles.facts}>
					{built?.check == null ? null : (
						<div className={styles.fact}>
							<dt>The check</dt>
							<dd>
								exit {built.check.exit_code}
								{built.head_commit === null ? null : (
									<>
										{" "}
										<code className={styles.code}>
											{built.head_commit.slice(0, 7)}
										</code>
									</>
								)}
							</dd>
						</div>
					)}
					{review === undefined ? null : (
						<div className={styles.fact}>
							<dt>The review</dt>
							<dd>
								<Status tone={verdictWord(review.verdict).tone}>
									{verdictWord(review.verdict).word}
								</Status>{" "}
								{review.current ? "head" : "older commit"}
							</dd>
						</div>
					)}
					<div className={styles.fact}>
						<dt>Runs</dt>
						<dd>
							{story.runs.length}
							{total === 0 ? null : ` · ${took(total)}`}
						</dd>
					</div>
					{tokens ? (
						<div className={styles.fact}>
							<dt>Tokens</dt>
							<dd>
								{COUNT.format(read)} in · {COUNT.format(wrote)} out
							</dd>
						</div>
					) : null}
					<div className={styles.fact}>
						<dt>Fix rounds</dt>
						<dd>
							{roundsUsed(story.rounds)} of {story.most_rounds} used
						</dd>
					</div>
				</dl>
				<RoundSteps rounds={story.rounds} most={story.most_rounds} />
			</div>
		</Panel>
	);
}

/** The fix rounds a card has used: the highest round number, since a round
 * tried again outside the card shows more than once, and the owner's
 * comment is no round. */
function roundsUsed(rounds: Round[]): number {
	return rounds.reduce((most, round) => Math.max(most, round.round ?? 0), 0);
}

/** The steps bar of section 4, `StepsBar`: one pill per round the project
 * gives, those used done, the round under way current. Named by the number
 * it shows. */
function RoundSteps({ rounds, most }: { rounds: Round[]; most: number }) {
	const used = roundsUsed(rounds);
	const running = rounds.some(
		(round) => round.round === used && round.result === null,
	);
	return (
		<StepsBar
			label={`Fix rounds: ${used} of ${most} used`}
			className={styles.steps}
			steps={Array.from({ length: most }, (_, index) => {
				const step = index + 1;
				return step < used || (step === used && !running)
					? "done"
					: step === used
						? "current"
						: "to-do";
			})}
		/>
	);
}

/** A link to the code host, in a new tab, when its address is https; the
 * words alone otherwise, since the address is data. */
function Link({ url, label }: { url: string; label: string }) {
	return linkable(url) ? (
		<a className={styles.link} href={url} target="_blank" rel="noreferrer">
			{label}
		</a>
	) : (
		<span>{label}</span>
	);
}

/** Where a failed run's cause lay, said after its reason: a card's run's in
 * the card or outside it, and a feature's in its job or outside it. */
export const LAY = {
	card: {
		in: " It lay in the card, which has to change.",
		outside: " It lay outside the card, so it goes back in the queue.",
	},
	feature: {
		in: " It lay in the job, so the feature waits for you.",
		outside: " It lay outside the job, so it runs again.",
	},
};

/** One run on an inset: its kind and outcome, who ran it and when, for a
 * failure why and where it lay, how it logged in and what it used, and the
 * check with its output behind Show. A card's runs and a feature's are shown
 * alike, `of` saying whose it is. */
export function RunRow({
	run,
	of = "card",
}: {
	run: Run;
	of?: keyof typeof LAY;
}) {
	const [showing, setShowing] = useState(false);
	const outputId = useId();
	const result =
		run.result === null
			? { word: "Running", tone: "running" as StatusTone }
			: (RESULTS[run.result] ?? {
					word: run.result,
					tone: "idle" as StatusTone,
				});
	return (
		<li className={styles.run}>
			<span className={styles.head}>
				<span className={styles.kind}>{KINDS[run.kind] ?? run.kind}</span>
				<Status tone={result.tone}>{result.word}</Status>
			</span>
			<span className={styles.meta}>
				<span>{run.worker}</span>
				<span>
					started{" "}
					<time dateTime={run.started_at}>
						{DATE_TIME.format(new Date(run.started_at))}
					</time>
				</span>
				{run.duration_ms === null ? null : (
					<span>took {took(run.duration_ms)}</span>
				)}
			</span>
			{run.reason === null ? null : (
				<span className={styles.text}>
					{run.reason}
					{run.in_card === null
						? null
						: run.in_card
							? LAY[of].in
							: LAY[of].outside}
				</span>
			)}
			{run.auth_method === null &&
			run.input_tokens === null &&
			run.output_tokens === null ? null : (
				<span className={styles.meta}>
					{run.auth_method === null ? null : (
						<span>Logged in with {run.auth_method}</span>
					)}
					{run.input_tokens === null && run.output_tokens === null ? null : (
						<span>
							Tokens {COUNT.format(run.input_tokens ?? 0)} in,{" "}
							{COUNT.format(run.output_tokens ?? 0)} out
						</span>
					)}
				</span>
			)}
			{run.check === null ? null : (
				<span className={styles.check}>
					<span className={styles.meta}>
						<span>
							Check exit {run.check.exit_code}
							{run.head_commit === null
								? null
								: ` on ${run.head_commit.slice(0, 7)}`}
						</span>
						<button
							type="button"
							className={styles.show}
							aria-expanded={showing}
							aria-controls={outputId}
							onClick={() => setShowing((shown) => !shown)}
						>
							{showing ? "Hide" : "Show"}
						</button>
					</span>
					{showing ? (
						<pre id={outputId} className={styles.output}>
							{run.check.output}
						</pre>
					) : null}
				</span>
			)}
		</li>
	);
}

/** One round on an inset: a fix round or the owner's comment, how its fix
 * went, what sent the card back, his words as he wrote them, the developer's
 * reply to each finding, and the commit the fix left with the review of it. */
export function RoundRow({
	round,
	reviews,
	most,
}: {
	round: Round;
	reviews: Review[];
	/** The rounds the project gives, which a round is counted against. */
	most: number;
}) {
	const name =
		round.round === null ? "Your comment" : `Round ${round.round} of ${most}`;
	const result =
		round.result === null
			? { word: "Running", tone: "running" as StatusTone }
			: (RESULTS[round.result] ?? {
					word: round.result,
					tone: "idle" as StatusTone,
				});
	const asked = reviews.find((review) => review.run_id === round.review_run_id);
	const after = reviews.find(
		(review) => review.run_id === round.review_after_run_id,
	);
	return (
		<li className={styles.run} aria-label={name}>
			<span className={styles.head}>
				<span className={styles.kind}>{name}</span>
				<Status tone={result.tone}>{result.word}</Status>
			</span>
			{round.round === null ? (
				<span className={styles.text}>You sent it back with a comment.</span>
			) : asked === undefined ? null : (
				<span className={styles.text}>
					The review of{" "}
					<code className={styles.code}>{asked.commit.slice(0, 7)}</code> asked
					for changes.
				</span>
			)}
			<Section label="What you wrote" items={round.comments}>
				{(comment) => <span className={styles.said}>{comment}</span>}
			</Section>
			<Section label="Replies" items={round.replies}>
				{(reply) => (
					<>
						<Inline text={reply.finding} />
						<span className={styles.sub}>
							<Inline text={reply.answer} />
						</span>
					</>
				)}
			</Section>
			{round.fixed_commit === null ? null : (
				<span className={styles.text}>
					It left{" "}
					<code className={styles.code}>{round.fixed_commit.slice(0, 7)}</code>
					{round.result !== "succeeded"
						? "."
						: after === undefined
							? ", not reviewed yet."
							: `, and the review of it ${AFTER[after.verdict] ?? `said ${after.verdict}`}.`}
				</span>
			)}
		</li>
	);
}

/** One review on an inset: the commit it is for and its verdict, whether it
 * counts for the card's head, the reviewer's summary, its findings, blocking
 * ones first, and its view on each decision marked for the owner. */
export function ReviewRow({ review }: { review: Review }) {
	const verdict = verdictWord(review.verdict);
	const findings = [...review.findings].sort(
		(one, other) => Number(other.blocking) - Number(one.blocking),
	);
	return (
		<li className={`${styles.run} ${styles.review}`}>
			<span className={styles.head}>
				<span className={styles.kind}>
					Review of{" "}
					<code className={styles.code}>{review.commit.slice(0, 7)}</code>
				</span>
				<Status tone={verdict.tone}>{verdict.word}</Status>
			</span>
			<span className={styles.meta}>
				<span>
					<time dateTime={review.reviewed_at}>
						{DATE_TIME.format(new Date(review.reviewed_at))}
					</time>
				</span>
				<span>
					{review.current
						? "For the pull request's head"
						: "For an older commit, so it does not count"}
				</span>
				{review.posted ? null : <span>Not on the pull request yet</span>}
			</span>
			{review.summary.trim() === "" ? null : (
				<span className={styles.text}>
					<Inline text={review.summary} />
				</span>
			)}
			<Section label="Findings" items={findings}>
				{(finding) => (
					<>
						<span className={styles.mark}>
							{finding.blocking ? "Blocking" : "Not blocking"}
						</span>{" "}
						{finding.file.trim() === "" ? null : (
							<>
								<code className={styles.code}>
									{finding.line > 0
										? `${finding.file}:${finding.line}`
										: finding.file}
								</code>{" "}
							</>
						)}
						<Inline text={finding.what} />
						{finding.why.trim() === "" ? null : (
							<span className={styles.sub}>
								Because <Inline text={finding.why} />
							</span>
						)}
					</>
				)}
			</Section>
			<Section label="For the owner" items={review.for_owner}>
				{(view) => (
					<>
						<Inline text={view.decision} />
						<span className={styles.sub}>
							<Inline text={view.view} />
						</span>
					</>
				)}
			</Section>
		</li>
	);
}

/** What the card was read as before it was built: the outcome and when, each
 * question with the answer it is built on and why, and what it lacks when it
 * is too thin; then, for a card read again, the readings before, as they were.
 * Every text is the reader's, shown as text. */
function Reading({
	reading,
	earlier,
	state,
}: {
	reading: CardReading;
	/** The readings before the newest, newest first. */
	earlier: CardReading[];
	state: string;
}) {
	const earlierId = useId();
	const listId = useId();
	const [showing, setShowing] = useState(false);
	const waits = state === "needs_answers";
	const { word, tone } = waits
		? { word: "Waits for your answers", tone: "needs-you" as const }
		: reading.answered
			? { word: "Built on your answers", tone: "idle" as const }
			: (OUTCOMES[reading.outcome] ?? {
					word: reading.outcome,
					tone: "idle" as const,
				});
	return (
		<Panel title="The reading">
			<div className={styles.body}>
				<p className={styles.state}>
					<Status tone={tone}>{word}</Status>
					<span className={styles.reason}>
						Read on {DATE_TIME.format(new Date(reading.read_at))}
					</span>
				</p>
				{reading.questions.length === 0 ? null : (
					<p className={styles.muted}>{questionsLine(reading, state)}</p>
				)}
				<Section label="Questions" items={reading.questions}>
					{(question) => (
						<>
							<Inline text={question.question} />
							<span className={styles.sub}>
								<span className={styles.mark}>
									{waits ? "Recommended" : "Answer"}
								</span>{" "}
								<Inline text={question.answer} />
								{reading.answered && question.recommended
									? " (recommended)"
									: null}
							</span>
							{question.why.trim() === "" ? null : (
								<span className={styles.sub}>
									Because <Inline text={question.why} />
								</span>
							)}
						</>
					)}
				</Section>
				<Section label="Missing" items={reading.missing}>
					{(missing) => <Inline text={missing} />}
				</Section>
				{earlier.length === 0 ? null : (
					<div className={styles.section}>
						<h3 id={earlierId} className={styles.label}>
							Earlier readings
						</h3>
						<p className={styles.muted}>
							It was read again before it was built. The readings before are
							kept as they were, with any answers given to them, and none is
							carried into its build.
						</p>
						<p className={styles.text}>
							<button
								type="button"
								className={styles.show}
								aria-expanded={showing}
								aria-controls={listId}
								onClick={() => setShowing((shown) => !shown)}
							>
								{showing
									? "Hide the earlier readings"
									: earlier.length === 1
										? "Show the earlier reading"
										: `Show the ${earlier.length} earlier readings`}
							</button>
						</p>
						{showing ? (
							<ul
								id={listId}
								aria-labelledby={earlierId}
								className={styles.runs}
							>
								{earlier.map((one) => (
									<EarlierReading key={one.read_at} reading={one} />
								))}
							</ul>
						) : null}
					</div>
				)}
			</div>
		</Panel>
	);
}

/** A reading before the newest, on an inset: when it was read and what it
 * answered, each question with the answer the owner gave or the reader's
 * recommendation, and what it lacked. */
function EarlierReading({ reading }: { reading: CardReading }) {
	const when = `Read on ${DATE_TIME.format(new Date(reading.read_at))}`;
	const { word, tone } = EARLIER[reading.outcome] ?? {
		word: reading.outcome,
		tone: "idle" as const,
	};
	return (
		<li className={`${styles.run} ${styles.review}`} aria-label={when}>
			<span className={styles.head}>
				<span className={styles.kind}>{when}</span>
				<Status tone={tone}>{reading.answered ? "Answered" : word}</Status>
			</span>
			<Section label="Questions then" items={reading.questions}>
				{(question) => (
					<>
						<Inline text={question.question} />
						<span className={styles.sub}>
							<span className={styles.mark}>
								{reading.answered ? "Your answer" : "Recommended"}
							</span>{" "}
							<Inline text={question.answer} />
							{reading.answered && question.recommended
								? " (recommended)"
								: null}
						</span>
					</>
				)}
			</Section>
			<Section label="Missing then" items={reading.missing}>
				{(missing) => <Inline text={missing} />}
			</Section>
		</li>
	);
}

/** What the questions are to the card: waiting for the owner's answers under
 * Waiting for you, answered by him, or built on the reader's recommendations
 * that Dev flow posts on it. */
function questionsLine(reading: CardReading, state: string): string {
	if (state === "needs_answers")
		return "The card leaves choices open that it does not settle. It waits for your answers under Questions before starting on Waiting for you, and nothing is posted on it until you send them.";
	if (reading.answered)
		return "The card leaves choices open that it does not settle, and it is built on your answers, posted on it as your comment.";
	return `The card leaves choices open that it does not settle, so it is built on the reader's recommendations. ${
		reading.commented
			? "Dev flow posted them on the card."
			: "Dev flow posts them on the card."
	}`;
}

/** One of the report's lists under its label, with an optional mono count
 * after it, each item on an inset, or with `plain` as a plain row; not shown
 * when it is empty. */
function Section<T>({
	label,
	count,
	items,
	plain = false,
	level = 3,
	children,
}: {
	label: string;
	count?: string;
	items: T[];
	plain?: boolean;
	/** The label's heading level: 3 under a card's title, 4 under a
	 * timeline entry's. */
	level?: 3 | 4;
	children: (item: T) => React.ReactNode;
}) {
	const id = useId();
	if (items.length === 0) return null;
	const Heading = level === 4 ? "h4" : "h3";
	return (
		<div className={styles.section}>
			{/* The list is named by the label alone, not its count. */}
			<Heading className={styles.label}>
				<span id={id}>{label}</span>
				{count === undefined ? null : (
					<>
						{" "}
						<span className={styles.count}>{count}</span>
					</>
				)}
			</Heading>
			<ul
				aria-labelledby={id}
				className={plain ? styles.plainItems : styles.items}
			>
				{items.map((item, index) => (
					// A report's items have no id of their own, and the list is
					// read whole.
					// biome-ignore lint/suspicious/noArrayIndexKey: see above
					<li key={index} className={plain ? styles.plainItem : styles.item}>
						{children(item)}
					</li>
				))}
			</ul>
		</div>
	);
}
