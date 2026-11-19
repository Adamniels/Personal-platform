// A card's page, /devflow/projects/{project}/cards/{number}, under the Projects tab, opened from the
// project's Cards panel: the card's whole story so far, read from GET
// /devflow/api/projects/{id}/cards/{number}, what Dev flow stored and never the code host. Its state
// and why, links to its issue and its pull request, each round it was sent back for, newest
// first, each review with its verdict, newest first, each run, newest first, and the latest
// report.
// Every text came from the code host, a worker or an agent and is shown as text. See "The pages"
// and "A card's page" in devflow/README.md.
import {
	EmptyState,
	Card as Panel,
	Status,
	type StatusTone,
} from "@platform/ui";
import { useEffect, useId, useState } from "react";
import { plainClick } from "./address";
import type { components } from "./api/schema";
import styles from "./card.module.css";
import { stateWord } from "./cards";
import { Inline } from "./format";
import { cardScreenshot, Screenshots } from "./screenshots";
import { linkable } from "./waiting";

export type CardStory = components["schemas"]["CardStoryView"];
type Run = CardStory["runs"][number];
type Report = NonNullable<CardStory["report"]>;
type Review = CardStory["reviews"][number];
type Round = CardStory["rounds"][number];

export const NOT_READ = "The card could not be read. Try again.";

/** How the page reads a card, Dev flow's API in the page and a test's own
 * in a test. Null when the caller has no such card. */
export type CardApi = {
	loadCard: (project: string, number: number) => Promise<CardStory | null>;
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

const KINDS: Record<string, string> = {
	build: "Build",
	review: "Review",
	fix: "Fix",
};

/** What the review of a fix's commit said of it, after "the review of it". */
const AFTER: Record<string, string> = {
	approve: "approved it",
	changes: "asked for changes",
};

/** The most rounds reviews start for a card. */
const MOST_ROUNDS = 3;

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

/** The page: the way back, the card's header and its three panels, or a not
 * found state; nothing before the card is read. */
export function CardPage({
	project,
	number,
	back,
	backPath,
	loadCard,
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
	return (
		<>
			<header className={styles.header}>
				{backLink}
				<h2 className={styles.name}>{story.title}</h2>
			</header>
			<Panel title="The card" className={styles.panel}>
				<div className={styles.body}>
					<p className={styles.state}>
						<Status tone={tone}>{word}</Status>
						{story.reason === null ? null : (
							<span className={styles.reason}>{story.reason}</span>
						)}
					</p>
					<p className={styles.links}>
						<Link url={story.web_url} label={`Issue #${story.number}`} />
						{story.pull_request === null ? null : (
							<Link
								url={story.pull_request.url}
								label={`Pull request #${story.pull_request.number}`}
							/>
						)}
					</p>
				</div>
			</Panel>
			{story.screenshots === null ? null : (
				<Panel title="Screenshots" className={styles.panel}>
					<div className={styles.body}>
						<p className={styles.muted}>
							Taken by the worker of{" "}
							<code className={styles.code}>
								{story.screenshots.commit.slice(0, 7)}
							</code>
							, after its check passed
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
			{story.rounds.length === 0 ? null : (
				<Panel title="Rounds" className={styles.panel}>
					<ul className={styles.runs} aria-label="Rounds">
						{story.rounds.map((round) => (
							<RoundRow
								key={round.run_id}
								round={round}
								reviews={story.reviews}
							/>
						))}
					</ul>
				</Panel>
			)}
			{story.reviews.length === 0 ? null : (
				<Panel title="Reviews" className={styles.panel}>
					<ul className={styles.runs} aria-label="Reviews">
						{story.reviews.map((review) => (
							<ReviewRow key={review.run_id} review={review} />
						))}
					</ul>
				</Panel>
			)}
			<Panel title="Runs" className={styles.panel}>
				{story.runs.length === 0 ? (
					<p className={styles.muted}>No run has taken this card yet.</p>
				) : (
					<ul className={styles.runs} aria-label="Runs">
						{story.runs.map((run) => (
							<RunRow key={run.id} run={run} />
						))}
					</ul>
				)}
			</Panel>
			{story.report === null ? null : <LatestReport report={story.report} />}
		</>
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

/** One run on an inset: its kind and outcome, who ran it and when, for a
 * failure why and where it lay, how it logged in and what it used, and the
 * check with its output behind Show. */
function RunRow({ run }: { run: Run }) {
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
							? " It lay in the card, which has to change."
							: " It lay outside the card, so it goes back in the queue."}
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
function RoundRow({ round, reviews }: { round: Round; reviews: Review[] }) {
	const name =
		round.round === null
			? "Your comment"
			: `Round ${round.round} of ${MOST_ROUNDS}`;
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
function ReviewRow({ review }: { review: Review }) {
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

/** The latest report, in its form: the summary, then each list the agent
 * filled, each under its own label. */
function LatestReport({ report }: { report: Report }) {
	return (
		<Panel title="The latest report" className={styles.panel}>
			<div className={styles.body}>
				{report.summary.trim() === "" ? null : (
					<p className={styles.text}>
						<Inline text={report.summary} />
					</p>
				)}
				<Section label="Tasks" items={report.tasks}>
					{(task) => (
						<>
							<span className={styles.mark}>
								{task.done ? "Done" : "Not done"}
							</span>{" "}
							<Inline text={task.text} />
						</>
					)}
				</Section>
				<Section label="Decisions" items={report.decisions}>
					{(decision) => (
						<>
							{decision.for_owner ? (
								<span className={styles.mark}>For you</span>
							) : null}{" "}
							<Inline text={decision.chosen} />
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
						</>
					)}
				</Section>
				<Section label="Tests" items={report.tests}>
					{(test) => (
						<>
							<Inline text={test.what} />:{" "}
							<span className={styles.mark}>{test.result}</span>
						</>
					)}
				</Section>
				<Section label="Pages" items={report.pages}>
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
				<Section label="Follow ups" items={report.follow_ups}>
					{(followUp) => <Inline text={followUp} />}
				</Section>
			</div>
		</Panel>
	);
}

/** One of the report's lists under its label, each item on an inset; not
 * shown when it is empty. */
function Section<T>({
	label,
	items,
	children,
}: {
	label: string;
	items: T[];
	children: (item: T) => React.ReactNode;
}) {
	const id = useId();
	if (items.length === 0) return null;
	return (
		<div className={styles.section}>
			<h3 id={id} className={styles.label}>
				{label}
			</h3>
			<ul aria-labelledby={id} className={styles.items}>
				{items.map((item, index) => (
					// A report's items have no id of their own, and the list is
					// read whole.
					// biome-ignore lint/suspicious/noArrayIndexKey: see above
					<li key={index} className={styles.item}>
						{children(item)}
					</li>
				))}
			</ul>
		</div>
	);
}
