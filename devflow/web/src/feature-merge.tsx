// Merging a feature into the default branch, as the owner, from its page and from its item under
// "Features ready for your review": "Merge into <default>", offered only when CI passed on the
// feature's head, asks first. The dialog reads what merging confirms, the pull request and its
// commit, whether the review approved it, its open findings when it did not, and how many decisions
// he has not answered, and the merge names the commit it showed, so a branch that moved on is
// refused, never merged. A refusal is shown in words where the button is, and what it is on is read
// again, and the nav bar asked to ask again. Every text came from the code host or the reviewer and
// is shown as text. See "Merging a feature" in devflow/README.md.
import { Button, Dialog, useToast } from "@platform/ui";
import { type ReactNode, useId, useState } from "react";
import { describe, EXPIRED, type LinkState, UNLINKED } from "./actions";
import type { components } from "./api/schema";
import { Inline } from "./format";
import { askNavAgain } from "./nav-attention";
import styles from "./waiting.module.css";

export type FeatureMergeOffer = components["schemas"]["FeatureMergeOffer"];
export type FeatureMergeConfirmation =
	components["schemas"]["FeatureMergeConfirmation"];

/** How a feature's merge is confirmed and made, Dev flow's API in the page
 * and a test's own in a test. */
export type FeatureMergeApi = {
	/** What merging the feature confirms, read as the dialog opens. */
	loadFeatureMerge: (
		project: string,
		number: number,
	) => Promise<FeatureMergeConfirmation>;
	/** Merges the feature, naming the commit the dialog showed. */
	mergeFeature: (
		project: string,
		number: number,
		headCommit: string,
	) => Promise<void>;
};

export type MergeFeatureProps = {
	project: string;
	number: number;
	offer: FeatureMergeOffer;
	link: LinkState;
	api: FeatureMergeApi;
	/** Reads again what the button is on, the page or the list. */
	reload: () => Promise<unknown>;
	/** Whether the button is its view's one primary button. */
	primary: boolean;
	/** What follows the button in its row, such as Open on GitHub. */
	after?: ReactNode;
};

/** "1 open finding", "2 open findings". */
function findings(count: number): string {
	return `${count} open ${count === 1 ? "finding" : "findings"}`;
}

/** "Merging confirms 1 decision you have not answered." */
export function confirms(count: number): string {
	return `Merging confirms ${count} ${count === 1 ? "decision" : "decisions"} you have not answered.`;
}

/** What the review says of the commit, in words. */
export function reviewLine(confirmation: FeatureMergeConfirmation): string {
	if (confirmation.review === "approved")
		return "The review approved this commit.";
	if (confirmation.review === "changes")
		return `The review asked for changes: ${findings(confirmation.open_findings.length)}`;
	return "Not reviewed as a whole";
}

/**
 * The button, why it cannot be pressed when the link is not usable, the
 * refusal of the last try, and, once pressed and read, the dialog.
 */
export function MergeFeature({
	project,
	number,
	offer,
	link,
	api,
	reload,
	primary,
	after,
}: MergeFeatureProps) {
	const toast = useToast();
	const lineId = useId();
	const [reading, setReading] = useState(false);
	const [confirmation, setConfirmation] =
		useState<FeatureMergeConfirmation | null>(null);
	const [refusal, setRefusal] = useState<string | null>(null);
	const line =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;

	async function open() {
		setReading(true);
		try {
			setConfirmation(await api.loadFeatureMerge(project, number));
			setRefusal(null);
		} catch (error) {
			// Refused, it is no longer what the page showed: read it again.
			setRefusal(describe(error));
			await reload();
		} finally {
			setReading(false);
		}
	}

	async function merge(shown: FeatureMergeConfirmation) {
		try {
			await api.mergeFeature(project, number, shown.head_commit);
			setRefusal(null);
			toast.success(`Merged feature #${number}`);
		} catch (error) {
			setRefusal(describe(error));
		}
		setConfirmation(null);
		await reload();
		// Whatever it answered, a merge may change what needs the owner.
		askNavAgain();
	}

	return (
		<div className={styles.footer}>
			{refusal === null ? null : (
				<p className={styles.refusal} role="alert">
					{refusal}
				</p>
			)}
			<div className={styles.actions}>
				<Button
					kind={primary ? "primary" : "secondary"}
					disabled={reading || link !== "linked"}
					aria-describedby={line === null ? undefined : lineId}
					onClick={open}
				>
					Merge into {offer.into}
				</Button>
				{after}
			</div>
			{line === null ? null : (
				<p id={lineId} className={styles.why}>
					{line}
				</p>
			)}
			{/* Rendered only while asked for, so the page holds no hidden text. */}
			{confirmation === null ? null : (
				<Dialog
					open
					title={`Merge feature #${number} into ${confirmation.into}?`}
					confirmLabel="Merge"
					onConfirm={() => merge(confirmation)}
					onCancel={() => setConfirmation(null)}
				>
					<div className={styles.confirm}>
						<p className={styles.text}>
							<Inline text={confirmation.title} />
						</p>
						<p className={styles.text}>
							Pull request #{confirmation.pull_request.number} at{" "}
							<code className={styles.commit}>
								{confirmation.head_commit.slice(0, 7)}
							</code>
						</p>
						<p className={styles.text}>{reviewLine(confirmation)}</p>
						{confirmation.open_findings.length === 0 ? null : (
							<ul className={styles.findings} aria-label="Open findings">
								{confirmation.open_findings.map((finding, index) => (
									// A verdict's findings have no id of their own, and the list
									// is read whole.
									// biome-ignore lint/suspicious/noArrayIndexKey: see above
									<li key={index} className={styles.finding}>
										{finding.file.trim() === "" ? null : (
											<>
												<code className={styles.commit}>
													{finding.line > 0
														? `${finding.file}:${finding.line}`
														: finding.file}
												</code>{" "}
											</>
										)}
										<Inline text={finding.what} />
										{finding.why.trim() === "" ? null : (
											<span className={styles.because}>
												Because <Inline text={finding.why} />
											</span>
										)}
									</li>
								))}
							</ul>
						)}
						{confirmation.unanswered === 0 ? null : (
							<p className={styles.text}>{confirms(confirmation.unanswered)}</p>
						)}
					</div>
				</Dialog>
			)}
		</div>
	);
}
