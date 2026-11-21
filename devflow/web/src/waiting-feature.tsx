// A feature of Dev flow's own that is the owner's turn, as "Waiting for you" shows it under
// "Features ready for your review": one that is ready, its latest review approving its head and CI
// passed there, or one that waits for him with why. It leads to its page, where its review, its
// cards and their decisions are; the item says what the review found and how many decisions he has
// not answered, links to its pull request, merges it into the default branch once CI passed on its
// head, after a confirmation, and sends the whole feature back with his comment. Every text came
// from the code host or an agent and is shown as text. See "Reviewing a feature", "Fixing a
// feature" and "Merging a feature" in devflow/README.md.
import { Card, Status } from "@platform/ui";
import { useId } from "react";
import type { ActionsApi, LinkState } from "./actions";
import { navigate, plainClick } from "./address";
import { stateWord } from "./cards";
import { MergeFeature } from "./feature-merge";
import { Inline } from "./format";
import { featurePath } from "./sections";
import { canSendBack, SendBack } from "./send-back-feature";
import { linkable, type WaitingList } from "./waiting";
import styles from "./waiting.module.css";
import { ReviewLine } from "./waiting-card";

export type FeatureItem = WaitingList["features"][number];

/** How many decisions wait for an answer, in words. */
export function unanswered(count: number): string {
	if (count === 0)
		return "Every decision its cards marked for you is answered.";
	return count === 1
		? "1 decision its cards marked for you is not answered yet."
		: `${count} decisions its cards marked for you are not answered yet.`;
}

export type WaitingFeatureProps = {
	feature: FeatureItem;
	link: LinkState;
	api: ActionsApi;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
};

export function WaitingFeature({
	feature,
	link,
	api,
	reload,
}: WaitingFeatureProps) {
	const { word, tone } = stateWord(feature.state);
	const project = `${feature.owner}/${feature.project_name}`;
	const page = featurePath(feature.project_id, feature.number);
	// Merging and sending back share the line saying why neither can be pressed.
	const linkLineId = useId();
	const links = (
		<>
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
				Open the feature
			</a>
			{feature.pull_request_url !== null &&
			linkable(feature.pull_request_url) ? (
				<a
					className={styles.open}
					href={feature.pull_request_url}
					target="_blank"
					rel="noreferrer"
				>
					Open on GitHub
				</a>
			) : null}
		</>
	);
	return (
		<Card
			title={feature.title}
			aside={<Status tone={tone}>{word}</Status>}
			className={styles.item}
			footer={
				// One column, so the merge's row and the send back stack rather
				// than share the card's footer row, at every width.
				<div className={styles.footer}>
					{feature.merge === null ? (
						<div className={styles.actions}>{links}</div>
					) : (
						<MergeFeature
							project={feature.project_id}
							number={feature.number}
							offer={feature.merge}
							link={link}
							api={api}
							reload={reload}
							primary={false}
							after={links}
							lineId={linkLineId}
						/>
					)}
					{canSendBack(feature.state, feature.pull_request_number) ? (
						<SendBack
							projectId={feature.project_id}
							number={feature.number}
							link={link}
							send={api.sendBackFeature}
							reload={reload}
							lineShownAt={feature.merge === null ? undefined : linkLineId}
						/>
					) : null}
				</div>
			}
		>
			<div className={styles.body}>
				<p className={styles.where}>
					<span>{project}</span> <span>Feature #{feature.number}</span>
					{feature.pull_request_number === null ? null : (
						<>
							{" "}
							<span>Pull request #{feature.pull_request_number}</span>
						</>
					)}
				</p>
				{feature.reason === null ? null : (
					<p className={styles.summary}>
						<Inline text={feature.reason} />
					</p>
				)}
				{feature.review === null ? null : (
					<ReviewLine review={feature.review} />
				)}
				<div className={styles.block}>
					<h3 className={styles.label}>Decisions</h3>
					<p className={styles.text}>{unanswered(feature.unanswered)}</p>
				</div>
			</div>
		</Card>
	);
}
