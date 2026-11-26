// A card of Dev flow's own that stopped, a feature the owner stopped, and a project whose runs keep
// failing outside their cards, as "Waiting for you" shows them under Stopped. A card Dev flow
// stopped says why, with "Comment to run again", which posts the owner's comment as him on the
// card's issue, and "Open on GitHub", and no Merge, as part 1 does for the runner's stopped items.
// A card the owner stopped says so, with "Run again" and, for a card on its own, "Drop", behind a
// dialog, in place of the comment; a feature he stopped says so, with "Run again". A failing project
// says Dev flow cannot run its cards, and why, so a broken machine is not silent. Every text in any
// of them came from the code host, a worker or an agent, and is shown as text. See "Stops" in
// devflow/README.md.
import { Button, Dialog, Status, TextArea, useToast } from "@platform/ui";
import { type FormEvent, useId, useState } from "react";
import {
	type ActionsApi,
	describe,
	EXPIRED,
	type LinkState,
	UNLINKED,
} from "./actions";
import { ArrowLink, Detail } from "./detail";
import { Inline } from "./format";
import { askNavAgain } from "./nav-attention";
import { RunFeatureAgain, STOPPED_BY_YOU } from "./run-again";
import { featurePath } from "./sections";
import type {
	FailingProjectItem,
	StoppedCardItem,
	StoppedFeatureItem,
} from "./waiting";
import styles from "./waiting.module.css";

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

export const RUN_AGAIN = "Comment to run again";

export type StoppedCardProps = {
	card: StoppedCardItem;
	link: LinkState;
	api: ActionsApi;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
	/** Whether a link is safe to make of the code host's address. */
	linkable: (address: string) => boolean;
};

/** One stopped card: one the owner stopped, with Run again and Drop, or one
 * Dev flow stopped, with why and the comment that runs it again; and its
 * issue on GitHub. */
export function StoppedCard(props: StoppedCardProps) {
	return props.card.stopped_by_owner ? (
		<StoppedByYouCard {...props} />
	) : (
		<StoppedByDevFlowCard {...props} />
	);
}

/** A card the owner stopped: Run again, the view's primary, and Drop, for a
 * card on its own, which asks first; no comment, since nothing went wrong in
 * it. */
function StoppedByYouCard({
	card,
	link,
	api,
	reload,
	linkable,
}: StoppedCardProps) {
	const toast = useToast();
	const lineId = useId();
	const [busy, setBusy] = useState(false);
	const [confirming, setConfirming] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const linked = link === "linked";
	// A card of a feature is picked by its feature's label, so it has none to
	// take off.
	const droppable = card.feature_number === null;
	// Nothing to say while the link's state is still being read.
	const line = !droppable
		? null
		: link === "none"
			? UNLINKED
			: link === "needs_relink"
				? EXPIRED
				: null;

	async function act(what: () => Promise<void>, done: string) {
		setBusy(true);
		try {
			await what();
			setRefusal(null);
			toast.success(done);
		} catch (error) {
			setRefusal(describe(error));
		} finally {
			setBusy(false);
			setConfirming(false);
		}
		await reload();
		askNavAgain();
	}

	return (
		<Detail
			title={card.title}
			where={[`${card.owner}/${card.project_name}`, `card #${card.number}`]}
			status={<Status tone="needs-you">{STOPPED_BY_YOU}</Status>}
			needsYou
			lede={
				<>
					You stopped it{" "}
					<time dateTime={card.stopped_at}>
						{DATE_TIME.format(new Date(card.stopped_at))}
					</time>
					. Run it again where it was, or drop it from Dev flow.
				</>
			}
			actions={
				<>
					{refusal === null ? null : (
						<p className={styles.refusal} role="alert">
							{refusal}
						</p>
					)}
					<div className={styles.actions}>
						<Button
							kind="primary"
							disabled={busy}
							onClick={() =>
								act(
									() => api.runCardAgain(card.project_id, card.number),
									`Running #${card.number} again`,
								)
							}
						>
							Run again
						</Button>
						{droppable ? (
							<Button
								kind="secondary"
								disabled={busy || !linked}
								aria-describedby={line === null ? undefined : lineId}
								onClick={() => setConfirming(true)}
							>
								Drop
							</Button>
						) : null}
						{linkable(card.web_url) ? (
							<a
								className={styles.open}
								href={card.web_url}
								target="_blank"
								rel="noreferrer"
							>
								Open on GitHub
							</a>
						) : null}
					</div>
					{line === null ? null : (
						<p id={lineId} className={styles.why}>
							{line}
						</p>
					)}
					{confirming ? (
						<Dialog
							open
							title={`Drop #${card.number}?`}
							kind="destructive"
							confirmLabel="Drop"
							onConfirm={() =>
								act(
									() => api.dropCard(card.project_id, card.number),
									`Dropped #${card.number}`,
								)
							}
							onCancel={() => setConfirming(false)}
						>
							<p className={styles.text}>
								Dev flow takes its label off and leaves its branch.
							</p>
						</Dialog>
					) : null}
				</>
			}
		/>
	);
}

/** A card Dev flow stopped: why, the comment that runs it again, and its
 * issue on GitHub. */
function StoppedByDevFlowCard({
	card,
	link,
	api,
	reload,
	linkable,
}: StoppedCardProps) {
	const toast = useToast();
	const lineId = useId();
	const [writing, setWriting] = useState(false);
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const linked = link === "linked";
	// Nothing to say while the link's state is still being read.
	const line =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;

	async function send(event: FormEvent) {
		event.preventDefault();
		if (text.trim() === "") return;
		setSending(true);
		try {
			await api.sendBackCard(card.project_id, card.number, text);
			setRefusal(null);
			setWriting(false);
			setText("");
			toast.success(`Commented on #${card.number}; it runs again`);
		} catch (error) {
			setRefusal(describe(error));
		} finally {
			setSending(false);
		}
		await reload();
		askNavAgain();
	}

	function cancel() {
		setWriting(false);
		setText("");
	}

	return (
		<Detail
			title={card.title}
			where={[`${card.owner}/${card.project_name}`, `card #${card.number}`]}
			status={<Status tone="failed">Stopped</Status>}
			needsYou={false}
			lede={
				<>
					Stopped{" "}
					<time dateTime={card.stopped_at}>
						{DATE_TIME.format(new Date(card.stopped_at))}
					</time>
					. Nothing more runs on it until you comment.
				</>
			}
			actions={
				<>
					{refusal === null ? null : (
						<p className={styles.refusal} role="alert">
							{refusal}
						</p>
					)}
					{writing ? (
						<form className={styles.comment} onSubmit={send}>
							<TextArea
								label="A comment runs it again."
								value={text}
								rows={4}
								onChange={(event) => setText(event.target.value)}
							/>
							<div className={styles.actions}>
								<Button
									type="submit"
									kind="primary"
									disabled={sending || text.trim() === ""}
								>
									Send
								</Button>
								<Button kind="ghost" onClick={cancel}>
									Cancel
								</Button>
							</div>
						</form>
					) : null}
					<div className={styles.actions}>
						{writing ? null : (
							<Button
								kind="primary"
								disabled={!linked}
								aria-describedby={line === null ? undefined : lineId}
								onClick={() => setWriting(true)}
							>
								{RUN_AGAIN}
							</Button>
						)}
						{linkable(card.web_url) ? (
							<a
								className={styles.open}
								href={card.web_url}
								target="_blank"
								rel="noreferrer"
							>
								Open on GitHub
							</a>
						) : null}
					</div>
					{line === null ? null : (
						<p id={lineId} className={styles.why}>
							{line}
						</p>
					)}
				</>
			}
		>
			<div className={`${styles.block} ${styles.danger}`}>
				<h3 className={styles.label}>Why it stopped</h3>
				<p className={styles.text}>
					<Inline text={card.reason} />
				</p>
			</div>
		</Detail>
	);
}

export type StoppedFeatureProps = {
	feature: StoppedFeatureItem;
	run: (projectId: string, number: number) => Promise<void>;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
	/** Whether a link is safe to make of the code host's address. */
	linkable: (address: string) => boolean;
};

/** What each state a feature is held in was doing when the owner stopped
 * it. */
const HELD_AT: Record<string, string> = {
	reviewing: "its review",
	fixing: "its fix",
	resolving: "its conflict",
};

/** A feature the owner stopped: that none of its jobs is handed out, Run
 * again, the view's primary, its page and its issue on GitHub. */
export function StoppedFeature({
	feature,
	run,
	reload,
	linkable,
}: StoppedFeatureProps) {
	return (
		<Detail
			title={feature.title}
			where={[
				`${feature.owner}/${feature.project_name}`,
				`feature #${feature.number}`,
			]}
			status={<Status tone="needs-you">{STOPPED_BY_YOU}</Status>}
			needsYou
			lede={
				<>
					You stopped {HELD_AT[feature.state] ?? "it"}{" "}
					<time dateTime={feature.stopped_at}>
						{DATE_TIME.format(new Date(feature.stopped_at))}
					</time>
					. None of its jobs is handed out until you run it again.
				</>
			}
			actions={
				<RunFeatureAgain
					projectId={feature.project_id}
					number={feature.number}
					run={run}
					reload={reload}
					after={
						linkable(feature.web_url) ? (
							<a
								className={styles.open}
								href={feature.web_url}
								target="_blank"
								rel="noreferrer"
							>
								Open on GitHub
							</a>
						) : null
					}
				/>
			}
		>
			<ArrowLink href={featurePath(feature.project_id, feature.number)}>
				Open the feature
			</ArrowLink>
		</Detail>
	);
}

/** A project whose runs keep failing outside their cards: that Dev flow
 * cannot run its cards, the last reason, and when it tries again. */
export function FailingProject({ project }: { project: FailingProjectItem }) {
	return (
		<Detail
			title={project.message}
			where={[`${project.owner}/${project.project_name}`]}
			status={<Status tone="failed">Cannot run</Status>}
			needsYou={false}
			lede={
				<>
					{project.failures} runs in a row failed outside their cards, the first{" "}
					<time dateTime={project.since}>
						{DATE_TIME.format(new Date(project.since))}
					</time>
					. Nothing is counted against the cards: Dev flow tries again at{" "}
					<time dateTime={project.wait_until}>
						{DATE_TIME.format(new Date(project.wait_until))}
					</time>
					, and this goes once a run succeeds.
				</>
			}
		>
			<div className={styles.block}>
				<h3 className={styles.label}>The last reason</h3>
				<p className={styles.text}>
					<Inline text={project.reason} />
				</p>
			</div>
		</Detail>
	);
}
