// A feature's page, /devflow/projects/{project}/features/{number}, under the Projects tab, opened
// from the project's Cards panel and from each of its cards' pages: the feature's whole story so
// far, read from GET /devflow/api/projects/{id}/features/{number}, what Dev flow stored and never
// the code host. Top to bottom: its title and state, with the reason and a link to its pull request
// once there is one, "Merge into <default>" while it is offered, and "Send the whole feature back"
// while it is ready or waits for you; the reviews of the whole feature, newest first, each with its
// findings and whether it covers the head; its fix rounds, each with the review that started it or
// your comment, the developer's replies and the review after it; what the agents want you to know,
// every decision its cards' latest reports marked for you, each answered "Fine as it is" here and
// nowhere else, sent back with its card, or confirmed by merging the feature; its branch with the
// command to check it out; every card's screenshots, by card; its cards in order, "Send back" on
// each that has landed while the feature is open, and those you sent back with your words; and its
// own runs, such as an agent resolving a conflict, as a card's page shows its runs. Every text came
// from the code host or an agent and is shown as text. See "A feature's page", "Sending a card
// back" and "Merging a feature" in devflow/README.md.
import {
	Badge,
	Button,
	EmptyState,
	Card as Panel,
	Status,
	type StatusTone,
	useToast,
} from "@platform/ui";
import { type ReactNode, useCallback, useEffect, useId, useState } from "react";
import { describe, EXPIRED, type LinkState, UNLINKED } from "./actions";
import { navigate, plainClick } from "./address";
import type { components } from "./api/schema";
import { ReviewRow, RoundRow, RunRow, verdictWord } from "./card";
import styles from "./card.module.css";
import { StepsBar, stateWord } from "./cards";
import { CommentBox } from "./comment-box";
import { short, type Tile, Tiles } from "./detail";
import own from "./feature.module.css";
import { confirms, type FeatureMergeApi, MergeFeature } from "./feature-merge";
import { Inline } from "./format";
import { askNavAgain } from "./nav-attention";
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

/** How the page reads a feature, answers a decision and merges the feature,
 * Dev flow's API in the page and a test's own in a test. */
export type FeatureApi = FeatureMergeApi & {
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
	/** Sends a card that landed back with the owner's comment, posted as
	 * him on its issue. */
	sendBackCard: (
		project: string,
		card: number,
		comment: string,
	) => Promise<void>;
	/** Sends a decision's card back with the owner's comment, the decision
	 * quoted above it, posted as him on the card's issue. */
	sendBackDecision: (
		project: string,
		number: number,
		run: string,
		position: number,
		comment: string,
	) => Promise<void>;
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
	/** The caller's GitHub link, which merging and commenting as him need;
	 * undefined until read. */
	link?: LinkState;
};

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

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
	loadFeatureMerge,
	mergeFeature,
	sendBack,
	sendBackCard,
	sendBackDecision,
	link,
	copy = clipboard,
}: FeaturePageProps) {
	const [story, setStory] = useState<FeatureStory | null>();
	const [failed, setFailed] = useState(false);
	// Merging and sending back share the line saying why neither can be pressed.
	const linkLineId = useId();

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
	const caption = `${story.repository} · Feature #${story.number} · ${count(
		story.cards.length,
		"card",
	)}`.toUpperCase();
	const unanswered = decisions.filter(
		({ decision }) => decision.answer === null,
	).length;
	const sendBackWhole = canSendBack(
		story.state,
		story.pull_request?.number ?? null,
	) ? (
		<SendBack
			projectId={project}
			number={number}
			link={link}
			send={sendBack}
			reload={load}
			lineShownAt={story.merge === null ? undefined : linkLineId}
		/>
	) : null;
	return (
		<>
			<header className={styles.header}>
				{backLink}
				<div className={styles.captionRow}>
					<Status tone={tone}>{word}</Status>
					<p className={styles.caption}>{caption}</p>
				</div>
				<h2 className={`${styles.name} ${styles.title}`}>{story.title}</h2>
				{story.reason === null ? null : (
					<p className={own.lede}>{story.reason}</p>
				)}
				<p className={styles.links}>
					<Link url={story.web_url} label={`Issue #${story.number}`} />
					{story.pull_request === null ? null : (
						<Link
							url={story.pull_request.url}
							label={`Pull request #${story.pull_request.number}`}
						/>
					)}
				</p>
			</header>
			<Panel
				title={`${story.state === "merged" ? "Merged" : "Merging"} into ${
					story.merge?.into ?? story.default_branch ?? "the default branch"
				}`}
				aside={
					headOf(story) === null ? undefined : (
						<span className={styles.aside}>
							head {short(headOf(story) ?? "")}
						</span>
					)
				}
				attention={MINE.includes(story.state)}
				className={styles.panel}
			>
				<div className={styles.body}>
					<Tiles tiles={mergeTiles(story)} />
					{story.merge === null ? (
						sendBackWhole
					) : (
						<MergeFeature
							project={project}
							number={number}
							offer={story.merge}
							link={link}
							api={{ loadFeatureMerge, mergeFeature }}
							reload={load}
							primary
							lineId={linkLineId}
							after={
								<>
									{sendBackWhole}
									{unanswered === 0 ? null : (
										<span className={own.confirms}>{confirms(unanswered)}</span>
									)}
								</>
							}
						/>
					)}
				</div>
			</Panel>
			{decisions.length === 0 ? null : (
				<Panel
					title="What the agents want you to know"
					aside={
						<span className={styles.aside}>
							{unanswered} open · {decisions.length - unanswered} answered
						</span>
					}
					className={styles.panel}
					attention={unanswered > 0}
				>
					<ul className={own.decisions} aria-label="Decisions for you">
						{decisions.map(({ card, decision }) => (
							<DecisionRow
								key={`${decision.run_id}/${decision.position}`}
								card={card}
								decision={decision}
								link={link}
								sendBack={
									card.can_send_back
										? (comment) =>
												sendBackDecision(
													project,
													number,
													decision.run_id,
													decision.position,
													comment,
												)
										: null
								}
								reload={load}
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
			<CardsPanel
				project={project}
				cards={story.cards}
				link={link}
				sendBack={(card, comment) => sendBackCard(project, card, comment)}
				reload={load}
			/>
			{pictured.length === 0 ? null : (
				<Panel
					title="Screenshots"
					aside={
						<span className={styles.aside}>
							{count(pictured.length, "card")} with pictures
						</span>
					}
					className={styles.panel}
				>
					<div className={own.shots}>
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
					title={`Fix rounds, ${story.fix_rounds} of ${story.most_rounds} used`}
					className={styles.panel}
				>
					<ul className={styles.runs} aria-label="Fix rounds of the feature">
						{story.rounds.map((round) => (
							<RoundRow
								key={round.run_id}
								round={round}
								reviews={story.reviews}
								most={story.most_rounds}
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
					) : story.state === "merged" ? (
						<p className={styles.text}>
							The feature is merged, and its branch is no longer watched.
						</p>
					) : (
						<>
							<p className={styles.text}>
								{story.state === "closed"
									? "Its pull request was closed without merging, so the feature is not in main. "
									: "The feature is not in main yet. "}
								To try it, check out its branch
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

/** The states in which the feature is the owner's turn, its merge card in
 * attention. */
const MINE = ["ready", "waiting_for_owner"];

/** "n things", or "1 thing". */
function count(n: number, one: string, many = `${one}s`): string {
	return `${n} ${n === 1 ? one : many}`;
}

/** The head the merge card names: the one the merge would name while it is
 * offered, otherwise the commit its branch is at; null without either. */
function headOf(story: FeatureStory): string | null {
	return story.merge?.head_commit ?? story.branch?.commit ?? null;
}

/** The merge card's tiles: CI on the head, the review of the whole feature,
 * and the decisions its cards marked for the owner. Merging is offered only
 * once CI passed on the head, so an offer, or a merge made, is CI passing. */
function mergeTiles(story: FeatureStory): Tile[] {
	const head = headOf(story);
	const ci: Tile =
		story.merge !== null
			? {
					label: "CI",
					tone: "done",
					status: "Passed",
					line: `on ${short(story.merge.head_commit)}`,
				}
			: story.state === "merged"
				? {
						label: "CI",
						tone: "done",
						status: "Passed",
						line: head === null ? "before the merge" : `on ${short(head)}`,
					}
				: {
						label: "CI",
						tone: "idle",
						status: "Not passed",
						line: "merging waits for it",
					};
	const newest = story.reviews[0];
	const review: Tile =
		newest === undefined
			? {
					label: "The review of the whole feature",
					tone: "idle",
					status: "Not reviewed",
					line: "not yet",
				}
			: {
					label: "The review of the whole feature",
					...toned(verdictWord(newest.verdict)),
					line: `${newest.current ? "covers the head" : "an older commit"} · ${
						newest.findings.filter((finding) => finding.blocking).length
					} open`,
				};
	const all = story.cards.flatMap((card) => card.decisions);
	const answered = all.filter((decision) => decision.answer !== null).length;
	const decisions: Tile =
		all.length === 0
			? {
					label: "Decisions for you",
					tone: "done",
					status: "None",
					line: "none marked for you",
				}
			: {
					label: "Decisions for you",
					tone: answered < all.length ? "needs-you" : "done",
					status: `${answered} of ${all.length} answered`,
					line: "below",
				};
	return [ci, review, decisions];
}

/** A verdict's word and tone as a tile's status and tone. */
function toned({ word, tone }: { word: string; tone: StatusTone }): {
	status: string;
	tone: StatusTone;
} {
	return { status: word, tone };
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

/** A card's title, linking to its page. */
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
			{card.title}
		</a>
	);
}

/** One decision. Open, an inset: its card's number and title in mono, what
 * was chosen in body large, the alternative and why, the reviewer's view,
 * and "Fine as it is" and, while its card can be sent back, "Send back #N"
 * with a comment box. Answered, one plain line: its card, what was chosen,
 * the answer as a status label and when. */
function DecisionRow({
	card,
	decision,
	answer,
	sendBack,
	link,
	reload,
}: {
	card: FeatureCard;
	decision: Decision;
	answer: () => Promise<void>;
	/** Sends its card back with the owner's comment; null while the card
	 * cannot be sent back. */
	sendBack: ((comment: string) => Promise<void>) | null;
	link: LinkState;
	/** Reads the feature again. */
	reload: () => Promise<unknown>;
}) {
	const toast = useToast();
	const lineId = useId();
	const [busy, setBusy] = useState(false);
	const [failed, setFailed] = useState(false);
	const [writing, setWriting] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const line = linkLine(link);
	const of = `#${card.number} · ${card.title}`;
	if (decision.answer) {
		const word =
			decision.answer.answer === "sent_back"
				? "Sent back"
				: decision.answer.answer === "merged"
					? "Confirmed by merging"
					: "Fine as it is";
		return (
			<li className={own.answered}>
				<span className={own.cardLine}>{of}</span>
				<span className={own.answeredLine}>
					<span className={own.answeredChosen}>
						<Inline text={decision.chosen} />
					</span>
					<Status tone="done">{word}</Status>
					<span className={own.when}>
						answered{" "}
						<time dateTime={decision.answer.answered_at}>
							{DATE_TIME.format(new Date(decision.answer.answered_at))}
						</time>
					</span>
				</span>
			</li>
		);
	}
	return (
		<li className={own.decision}>
			<span className={own.cardLine}>{of}</span>
			<p className={own.chosen}>
				<Inline text={decision.chosen} />
			</p>
			{decision.alternative.trim() === "" ? null : (
				<p className={own.because}>
					Instead of <Inline text={decision.alternative} />
				</p>
			)}
			{decision.why.trim() === "" ? null : (
				<p className={own.because}>
					Because <Inline text={decision.why} />
				</p>
			)}
			<p className={own.because}>
				<span className={styles.mark}>Reviewer:</span>{" "}
				{decision.reviewer_view === null ? (
					"gave no view on it."
				) : (
					<Inline text={decision.reviewer_view} />
				)}
			</p>
			{failed ? (
				<p className={styles.error} role="alert">
					{NOT_ANSWERED}
				</p>
			) : null}
			{refusal === null ? null : (
				<p className={styles.error} role="alert">
					{refusal}
				</p>
			)}
			{writing && sendBack !== null ? (
				<CommentBox
					label={`What should the developer change in #${card.number}?`}
					cancel={() => setWriting(false)}
					send={async (comment) => {
						let sent = false;
						try {
							await sendBack(comment);
							setRefusal(null);
							setWriting(false);
							toast.success(`Sent back #${card.number}`);
							sent = true;
						} catch (error) {
							setRefusal(describe(error));
						}
						await reload();
						askNavAgain();
						return sent;
					}}
				/>
			) : (
				<div className={own.actions}>
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
					{sendBack === null ? null : (
						<Button
							kind="secondary"
							disabled={busy || link !== "linked"}
							aria-label={`Send back #${card.number}: ${decision.chosen}`}
							aria-describedby={line === null ? undefined : lineId}
							onClick={() => setWriting(true)}
						>
							Send back #{card.number}
						</Button>
					)}
				</div>
			)}
			{sendBack === null || line === null ? null : (
				<p id={lineId} className={styles.sub}>
					{line}
				</p>
			)}
		</li>
	);
}

/** Why commenting as the owner cannot be done now, or null; nothing while
 * the link's state is still being read. */
function linkLine(link: LinkState): string | null {
	return link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;
}

/**
 * The Cards panel: how many landed of those counted, an ignored card in
 * neither, in mono and as a steps bar, one step per card; then each card in
 * its order as a row, "Send back" as a ghost on each that can be sent back,
 * which opens the comment box under its row, and under a card sent back what
 * you wrote.
 */
function CardsPanel({
	project,
	cards,
	link,
	sendBack,
	reload,
}: {
	project: string;
	cards: FeatureCard[];
	link: LinkState;
	sendBack: (card: number, comment: string) => Promise<void>;
	/** Reads the feature again. */
	reload: () => Promise<unknown>;
}) {
	const toast = useToast();
	const lineId = useId();
	const [writing, setWriting] = useState<number | null>(null);
	const [refusal, setRefusal] = useState<string | null>(null);
	const line = linkLine(link);
	const counted = cards.filter((card) => card.state !== "ignored");
	const landed = counted.filter((card) => card.state === "done").length;
	const progress = `${landed} of ${counted.length} landed`;
	// A feature's cards are built one at a time, in order: the first not
	// landed is the current one.
	const current = counted.findIndex((card) => card.state !== "done");
	return (
		<Panel
			title="Cards"
			aside={<span className={styles.aside}>{progress}</span>}
			className={styles.panel}
		>
			<div className={styles.body}>
				{counted.length === 0 ? null : (
					<StepsBar
						label={`Cards: ${progress}`}
						steps={counted.map((card, at) =>
							card.state === "done"
								? "done"
								: at === current
									? "current"
									: "to-do",
						)}
					/>
				)}
				{cards.some((card) => card.can_send_back) && line !== null ? (
					<p id={lineId} className={styles.muted}>
						{line}
					</p>
				) : null}
				<ol className={own.rows} aria-label="Cards of the feature">
					{cards.map((card) => (
						<CardRow
							key={card.number}
							project={project}
							card={card}
							canPress={link === "linked" && writing !== card.number}
							lineId={line === null ? undefined : lineId}
							open={() => {
								setRefusal(null);
								setWriting(card.number);
							}}
						>
							{refusal === null || writing !== card.number ? null : (
								<p className={styles.error} role="alert">
									{refusal}
								</p>
							)}
							{writing !== card.number ? null : (
								<CommentBox
									label={`What should the developer change in #${card.number}?`}
									cancel={() => setWriting(null)}
									send={async (comment) => {
										let sent = false;
										try {
											await sendBack(card.number, comment);
											setRefusal(null);
											setWriting(null);
											toast.success(`Sent back #${card.number}`);
											sent = true;
										} catch (error) {
											setRefusal(describe(error));
										}
										await reload();
										askNavAgain();
										return sent;
									}}
								/>
							)}
						</CardRow>
					))}
				</ol>
			</div>
		</Panel>
	);
}

/** A card as a row: its number in mono, its title linked to its page, a
 * badge with its open decisions, its state, "Landed" once done, and "Send
 * back" as a ghost while it can be sent back; on phone the state and Send
 * back drop under the title. Under the row, what it holds, the comment box
 * among them, and, for a card sent back, what you wrote, as you wrote it. */
function CardRow({
	project,
	card,
	canPress,
	lineId,
	open,
	children,
}: {
	project: string;
	card: FeatureCard;
	/** Whether Send back can be pressed now. */
	canPress: boolean;
	/** The line saying why Send back cannot be pressed, when there is one. */
	lineId?: string;
	/** Opens the comment box under the row. */
	open: () => void;
	children?: ReactNode;
}) {
	const { word, tone } =
		card.state === "done"
			? { word: "Landed", tone: stateWord(card.state).tone }
			: stateWord(card.state);
	const waiting = card.decisions.filter(
		(decision) => decision.answer === null,
	).length;
	const sent = card.sent_back;
	return (
		<li className={own.row}>
			<div className={own.rowHead}>
				<span className={own.number}>#{card.number}</span>
				<span className={own.rowTitle}>
					<CardLink project={project} card={card} />
					{waiting === 0 ? null : <Badge>{count(waiting, "decision")}</Badge>}
				</span>
				<span className={own.rowEnd}>
					<Status tone={tone}>{word}</Status>
					{card.can_send_back ? (
						<Button
							kind="ghost"
							disabled={!canPress}
							aria-label={`Send back #${card.number}`}
							aria-describedby={lineId}
							onClick={open}
						>
							Send back
						</Button>
					) : null}
				</span>
			</div>
			{children}
			{sent === null ? null : (
				<div className={own.sentBack}>
					<p className={styles.muted}>
						You sent it back {sent.times === 1 ? "once" : `${sent.times} times`}
						, from pull request #{sent.pull_request.number}:
					</p>
					{sent.comments.map((comment, at) => (
						// The same words may be written twice, so their place keys them.
						// biome-ignore lint/suspicious/noArrayIndexKey: comments have no id
						<p key={at} className={own.said}>
							{comment}
						</p>
					))}
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
