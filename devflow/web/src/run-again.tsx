// What the owner does with a card or a feature he stopped himself: "Run again", which puts a card
// back where its stopped run started from or releases a feature's hold, and, for a card on its own,
// "Drop", which takes its pick-up label off as him and leaves it ignored. Neither is a comment. A
// refusal is shown in words, what shows it is read again, and the nav bar asked to ask again. See
// "Acting from the page" and "Stops" in devflow/README.md.
import { Button, useToast } from "@platform/ui";
import { type ReactNode, useState } from "react";
import { describe } from "./actions";
import { askNavAgain } from "./nav-attention";
import styles from "./waiting.module.css";

/** What a card or a feature the owner stopped says. */
export const STOPPED_BY_YOU = "Stopped by you";

/** How a card or a feature the owner stopped is run again, or a card
 * dropped, Dev flow's API in the page and a test's own in a test. */
export type RunAgainApi = {
	/** Puts the card back where the run he stopped started from. */
	runCardAgain: (projectId: string, number: number) => Promise<void>;
	/** Takes the card's pick-up label off as him and leaves it ignored. */
	dropCard: (projectId: string, number: number) => Promise<void>;
	/** Releases the feature his stop of one of its runs holds. */
	runFeatureAgain: (projectId: string, number: number) => Promise<void>;
};

export type RunFeatureAgainProps = {
	projectId: string;
	number: number;
	run: RunAgainApi["runFeatureAgain"];
	/** Reads again what shows the feature. */
	reload: () => Promise<unknown>;
	/** What follows the button in its row, such as Open on GitHub. */
	after?: ReactNode;
};

/** "Run again" on a feature the owner stopped: the view's primary button,
 * with what went wrong last above it. */
export function RunFeatureAgain({
	projectId,
	number,
	run,
	reload,
	after,
}: RunFeatureAgainProps) {
	const toast = useToast();
	const [running, setRunning] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);

	async function press() {
		setRunning(true);
		try {
			await run(projectId, number);
			setRefusal(null);
			toast.success(`Running #${number} again`);
		} catch (error) {
			setRefusal(describe(error));
		} finally {
			setRunning(false);
		}
		await reload();
		askNavAgain();
	}

	return (
		<>
			{refusal === null ? null : (
				<p className={styles.refusal} role="alert">
					{refusal}
				</p>
			)}
			<div className={styles.actions}>
				<Button kind="primary" disabled={running} onClick={press}>
					Run again
				</Button>
				{after}
			</div>
		</>
	);
}
