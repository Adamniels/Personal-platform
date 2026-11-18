// A card's page, /devflow/projects/{project}/cards/{number}, under the Projects tab, opened from the
// project's Cards panel: the card's whole story so far, read from GET
// /devflow/api/projects/{id}/cards/{number}, what Dev flow stored and never the code host. Its state
// and why, links to its issue and its pull request, each run, newest first, and the latest report.
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
import { linkable } from "./waiting";

export type CardStory = components["schemas"]["CardStoryView"];
type Run = CardStory["runs"][number];
type Report = NonNullable<CardStory["report"]>;

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

const KINDS: Record<string, string> = { build: "Build", review: "Review" };

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
								{page.looked ? "Looked at" : "Not looked at"}
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
