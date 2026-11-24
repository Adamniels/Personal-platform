// A feature of Dev flow's own that is the owner's turn, as "Waiting for you" shows it under
// "Features ready for your review", in the detail's frame: one that is ready, its latest review
// approving its head and CI passed there, or one that waits for him with why. Its tiles say whether
// CI passed on its head, what the review found and how many decisions he has not answered, with
// "Open the feature" under them, its page, where its review, its cards and their decisions are.
// It merges into the default branch once CI passed on its head, after a confirmation, sends the
// whole feature back with his comment, and links to its pull request. Every text came from the
// code host or an agent and is shown as text. See "Reviewing a feature", "Fixing a feature" and
// "Merging a feature" in devflow/README.md.
import { Status } from "@platform/ui";
import { useId } from "react";
import type { ActionsApi, LinkState } from "./actions";
import { stateWord } from "./cards";
import {
	ArrowLink,
	Detail,
	reviewTile,
	short,
	type Tile,
	Tiles,
} from "./detail";
import { MergeFeature } from "./feature-merge";
import { Inline } from "./format";
import { featurePath } from "./sections";
import { canSendBack, SendBack } from "./send-back-feature";
import { linkable, type WaitingList } from "./waiting";
import styles from "./waiting.module.css";

export type FeatureItem = WaitingList["features"][number];

/** The feature's tiles: CI on its head, its review, and the decisions its
 * cards marked for the owner that he has not answered. Merging is offered
 * only once CI passed on the head, so an offer is CI passing. */
export function featureTiles(feature: FeatureItem): Tile[] {
	return [
		feature.merge === null
			? {
					label: "CI",
					tone: "idle",
					status: "Not passed",
					line: "merging waits for it",
				}
			: {
					label: "CI",
					tone: "done",
					status: "Passed",
					line: `on ${short(feature.merge.head_commit)}`,
				},
		reviewTile(feature.review, "the whole feature, not yet"),
		feature.unanswered > 0
			? {
					label: "Decisions",
					tone: "needs-you",
					status: `${feature.unanswered} for you`,
					line: "not answered yet",
				}
			: {
					label: "Decisions",
					tone: "done",
					status: "Answered",
					line: "every one marked for you",
				},
	];
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
	// Merging and sending back share the line saying why neither can be pressed.
	const linkLineId = useId();
	const sendBack = canSendBack(feature.state, feature.pull_request_number) ? (
		<SendBack
			projectId={feature.project_id}
			number={feature.number}
			link={link}
			send={api.sendBackFeature}
			reload={reload}
			lineShownAt={feature.merge === null ? undefined : linkLineId}
		/>
	) : null;
	const open =
		feature.pull_request_url !== null && linkable(feature.pull_request_url) ? (
			<a
				className={styles.open}
				href={feature.pull_request_url}
				target="_blank"
				rel="noreferrer"
			>
				Open on GitHub
			</a>
		) : null;
	return (
		<Detail
			title={feature.title}
			where={[
				`${feature.owner}/${feature.project_name}`,
				`feature #${feature.number}`,
				...(feature.pull_request_number === null
					? []
					: [`pull request #${feature.pull_request_number}`]),
				...(feature.merge === null ? [] : [short(feature.merge.head_commit)]),
			]}
			status={<Status tone={tone}>{word}</Status>}
			needsYou={tone === "needs-you"}
			lede={feature.reason === null ? null : <Inline text={feature.reason} />}
			actions={
				feature.merge === null ? (
					<div className={styles.actions}>
						{sendBack}
						{open}
					</div>
				) : (
					<MergeFeature
						project={feature.project_id}
						number={feature.number}
						offer={feature.merge}
						link={link}
						api={api}
						reload={reload}
						primary
						lineId={linkLineId}
						after={
							<>
								{sendBack}
								{open}
							</>
						}
					/>
				)
			}
		>
			<Tiles tiles={featureTiles(feature)} />
			<ArrowLink href={featurePath(feature.project_id, feature.number)}>
				Open the feature
			</ArrowLink>
			{feature.review === null || feature.review.summary === "" ? null : (
				<div className={styles.block}>
					<h3 className={styles.label}>The reviewer</h3>
					<p className={styles.text}>
						<Inline text={feature.review.summary} />
					</p>
				</div>
			)}
		</Detail>
	);
}
