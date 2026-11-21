// A feature's page, /devflow/projects/{project}/features/{number}, under the Projects tab, opened
// from the project's Cards panel and from each of its cards' pages: the feature's whole story so
// far, read from GET /devflow/api/projects/{id}/features/{number}, what Dev flow stored and never
// the code host. Top to bottom: its title and state, with the reason and a link to its pull request
// once there is one, and "Send the whole feature back" while it is ready or waits for you; the
// reviews of the whole feature, newest first, each with its findings and whether it covers the
// head; its fix rounds, each with the review that started it or your comment, the developer's
// replies and the review after it; what the agents want you to know, every decision its cards'
// latest reports marked for you, each answered "Fine as it is" here and nowhere else; its branch
// with the command to check it out; every card's screenshots, by card; its cards in order; and its
// own runs, such as an agent resolving a conflict, as a card's page shows its runs. Every text came
// from the code host or an agent and is shown as text. See "A feature's page" in
// devflow/README.md.
import {
	Button,
	EmptyState,
	Card as Panel,
	Status,
	Table,
	type TableColumn,
} from "@platform/ui";
import { useCallback, useEffect, useState } from "react";
import type { LinkState } from "./actions";
import { navigate, plainClick } from "./address";
import type { components } from "./api/schema";
import { ReviewRow, RoundRow, RunRow } from "./card";
import styles from "./card.module.css";
import { stateWord } from "./cards";
import own from "./feature.module.css";
import { Inline } from "./format";
import { Command } from "./project";
import { cardScreenshot, Screenshots } from "./screenshots";
import { cardPath } from "./sections";
import {
	canSendBack,
	SendBack,
	type SendBackFeature,
} from "./send-back-feature";
import { linkable } from "./waiting";

export type FeatureStory = components["schemas"]["FeatureStoryView"];
type FeatureCard = FeatureStory["cards"][number];
type Decision = FeatureCard["decisions"][number];
export type DecisionAnswer = components["schemas"]["DecisionAnswer"];

export const NOT_READ = "The feature could not be read. Try again.";
export const NOT_ANSWERED =
	"The answer could not be kept. Read the page again and try once more.";

/** How the page reads a feature and answers a decision, Dev flow's API in
 * the page and a test's own in a test. */
export type FeatureApi = {
	/** The feature, or null when the caller has no such feature. */
	loadFeature: (
		project: string,
		number: number,
	) => Promise<FeatureStory | null>;
	/** Answers the decision "Fine as it is", answering the answer kept. */
	answerFine: (
		project: string,
		number: number,
		run: string,
		position: number,
	) => Promise<DecisionAnswer>;
	/** Sends the whole feature back with the owner's comment, posted as him
	 * on its pull request. */
	sendBack: SendBackFeature;
	/** Copies text to the clipboard, the browser's by default. */
	copy?: (text: string) => Promise<void>;
};

export type FeaturePageProps = FeatureApi & {
	project: string;
	number: number;
	/** Goes back to the project's page. */
	back: () => void;
	/** The project page's address, for the way back. */
	backPath: string;
	/** The caller's GitHub link, which commenting as him needs. */
	link?: LinkState;
};

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

const COUNT = new Intl.NumberFormat("en-GB");

const clipboard = (text: string) => navigator.clipboard.writeText(text);

/** The command that checks the feature's branch out. */
export function checkout(branch: string): string {
	return `git fetch origin ${branch} && git switch ${branch}`;
}

/** The page: the way back, the feature's header and its panels, or a not
 * found state; nothing before the feature is read. */
export function FeaturePage({
	project,
	number,
	back,
	backPath,
	loadFeature,
	answerFine,
	sendBack,
	link,
	copy = clipboard,
}: FeaturePageProps) {
	const [story, setStory] = useState<FeatureStory | null>();
	const [failed, setFailed] = useState(false);

	const load = useCallback(
		() =>
			loadFeature(project, number).then(
				(answer) => {
					setStory(answer);
					setFailed(false);
				},
				() => setFailed(true),
			),
		[project, number, loadFeature],
	);

	useEffect(() => {
		let current = true;
		loadFeature(project, number).then(
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
	}, [project, number, loadFeature]);

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

	if (failed && !story) {
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
				title="There is no such feature"
				detail="Its label may have been taken off, or the project was disconnected."
				action={backLink}
			/>
		);
	}

	const { word, tone } = stateWord(story.state);
	const decisions = story.cards.flatMap((card) =>
		card.decisions.map((decision) => ({ card, decision })),
	);
	const pictured = story.cards.filter(
		(card) =>
			card.screenshots !== null &&
			(card.screenshots.groups.length > 0 || card.screenshots.note !== null),
	);
	const done = story.cards.filter((card) => card.state === "done").length;
	return (
		<>
			<header className={styles.header}>
				{backLink}
				<h2 className={styles.name}>{story.title}</h2>
			</header>
			<Panel title="The feature" className={styles.panel}>
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
						<span className={own.count}>
							{done} of {story.cards.length} cards done
						</span>
					</p>
					{canSendBack(story.state, story.pull_request?.number ?? null) ? (
						<SendBack
							projectId={project}
							number={number}
							link={link}
							send={sendBack}
							reload={load}
						/>
					) : null}
				</div>
			</Panel>
			{story.reviews.length === 0 ? null : (
				<Panel title="The review of the whole feature" className={styles.panel}>
					<ul className={styles.runs} aria-label="Reviews of the feature">
						{story.reviews.map((review) => (
							<ReviewRow key={review.run_id} review={review} />
						))}
					</ul>
				</Panel>
			)}
			{story.rounds.length === 0 ? null : (
				<Panel
					title={`Fix rounds, ${story.fix_rounds} of 3 used`}
					className={styles.panel}
				>
					<ul className={styles.runs} aria-label="Fix rounds of the feature">
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
			{decisions.length === 0 ? null : (
				<Panel
					title="What the agents want you to know"
					className={styles.panel}
					attention={decisions.some(({ decision }) => !decision.answer)}
				>
					<ul className={styles.runs} aria-label="Decisions for you">
						{decisions.map(({ card, decision }) => (
							<DecisionRow
								key={`${decision.run_id}/${decision.position}`}
								card={card}
								decision={decision}
								answer={async () => {
									try {
										const kept = await answerFine(
											project,
											number,
											decision.run_id,
											decision.position,
										);
										setStory((shown) => answered(shown, decision, kept));
									} catch (error) {
										await load();
										throw error;
									}
								}}
							/>
						))}
					</ul>
				</Panel>
			)}
			<Panel title="The branch" className={styles.panel}>
				<div className={styles.body}>
					{story.branch === null ? (
						<p className={styles.muted}>
							Its branch is made when its first card starts.
						</p>
					) : (
						<>
							<p className={styles.text}>
								The feature is not in main yet. To try it, check out its branch
								{story.branch.commit === null ? null : (
									<>
										, at{" "}
										<code className={styles.code}>
											{story.branch.commit.slice(0, 7)}
										</code>
									</>
								)}
								, or look at what the worker captured.
							</p>
							<Command
								label="Check out the branch"
								copyLabel="Copy the command"
								text={checkout(story.branch.name)}
								copy={copy}
							/>
						</>
					)}
				</div>
			</Panel>
			{pictured.length === 0 ? null : (
				<Panel title="Screenshots" className={styles.panel}>
					<div className={styles.body}>
						{pictured.map((card) => (
							<section
								key={card.number}
								className={styles.section}
								aria-label={`Screenshots of #${card.number}`}
							>
								<h3 className={styles.label}>
									#{card.number} {card.title}
								</h3>
								<Screenshots
									titled={false}
									address={(name) => cardScreenshot(project, card.number, name)}
									groups={card.screenshots?.groups ?? []}
									note={card.screenshots?.note ?? null}
								/>
							</section>
						))}
					</div>
				</Panel>
			)}
			<Panel title="Cards" className={styles.panel}>
				<Table
					label="Cards of the feature"
					columns={columns(project)}
					rows={story.cards}
					rowKey={(card) => String(card.number)}
				/>
			</Panel>
			{story.runs.length === 0 ? null : (
				<Panel title="Runs" className={styles.panel}>
					<ul className={styles.runs} aria-label="Runs of the feature">
						{story.runs.map((run) => (
							<RunRow key={run.id} run={run} of="feature" />
						))}
					</ul>
				</Panel>
			)}
		</>
	);
}

/** The story with the decision's answer as kept. */
function answered(
	story: FeatureStory | null | undefined,
	decision: Decision,
	kept: DecisionAnswer,
): FeatureStory | null | undefined {
	if (!story) return story;
	return {
		...story,
		cards: story.cards.map((card) => ({
			...card,
			decisions: card.decisions.map((each) =>
				each.run_id === decision.run_id && each.position === decision.position
					? { ...each, answer: kept }
					: each,
			),
		})),
	};
}

/** The cards' table: the card, linked to its page, its state, its fix
 * rounds, its tokens and its decisions for you, the numbers folded under the
 * card on phone. */
function columns(project: string): TableColumn<FeatureCard>[] {
	return [
		{
			key: "card",
			header: "Card",
			cell: (card) => <CardLink project={project} card={card} />,
		},
		{
			key: "state",
			header: "State",
			cell: (card) => {
				const { word, tone } = stateWord(card.state);
				return <Status tone={tone}>{word}</Status>;
			},
		},
		{
			key: "rounds",
			header: "Fix rounds",
			numeric: true,
			priority: 2,
			cell: (card) => card.fix_rounds,
			fold: (card) =>
				`${card.fix_rounds} ${card.fix_rounds === 1 ? "fix round" : "fix rounds"}`,
		},
		{
			key: "tokens",
			header: "Tokens",
			numeric: true,
			priority: 2,
			cell: (card) => COUNT.format(card.input_tokens + card.output_tokens),
			fold: (card) =>
				`${COUNT.format(card.input_tokens + card.output_tokens)} tokens`,
		},
		{
			key: "decisions",
			header: "For you",
			numeric: true,
			priority: 3,
			cell: (card) => card.decisions.length,
		},
	];
}

/** A card's number and title, linking to its page. */
function CardLink({ project, card }: { project: string; card: FeatureCard }) {
	const page = cardPath(project, card.number);
	return (
		<a
			className={own.card}
			href={page}
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					navigate(page);
				}
			}}
		>
			#{card.number} {card.title}
		</a>
	);
}

/** One decision on an inset: the card it is of, what was chosen, the
 * alternative and why, the reviewer's view, and "Fine as it is", or the
 * answer once given. */
function DecisionRow({
	card,
	decision,
	answer,
}: {
	card: FeatureCard;
	decision: Decision;
	answer: () => Promise<void>;
}) {
	const [busy, setBusy] = useState(false);
	const [failed, setFailed] = useState(false);
	return (
		<li className={`${styles.run} ${styles.review}`}>
			<span className={styles.kind}>
				#{card.number}: <Inline text={decision.chosen} />
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
			<span className={styles.text}>
				<span className={styles.mark}>Reviewer:</span>{" "}
				{decision.reviewer_view === null ? (
					<span className={styles.reason}>gave no view on it.</span>
				) : (
					<Inline text={decision.reviewer_view} />
				)}
			</span>
			{failed ? (
				<p className={styles.error} role="alert">
					{NOT_ANSWERED}
				</p>
			) : null}
			{decision.answer ? (
				<span className={styles.meta}>
					<Status tone="done">Fine as it is</Status>
					<span>
						answered{" "}
						<time dateTime={decision.answer.answered_at}>
							{DATE_TIME.format(new Date(decision.answer.answered_at))}
						</time>
					</span>
				</span>
			) : (
				<div>
					<Button
						kind="secondary"
						disabled={busy}
						aria-label={`Fine as it is: #${card.number}, ${decision.chosen}`}
						onClick={async () => {
							setBusy(true);
							setFailed(false);
							try {
								await answer();
							} catch {
								setFailed(true);
							} finally {
								setBusy(false);
							}
						}}
					>
						Fine as it is
					</Button>
				</div>
			)}
		</li>
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
