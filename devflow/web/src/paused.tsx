// Everything paused: whether every project of the owner is paused, read once for the whole page,
// and the strip that says so under the page's heading on every tab, with "Continue everything".
// The switch that pauses everything is on Running now. See "The pages" and "Jobs" in
// devflow/README.md.
import { Button } from "@platform/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { describe } from "./actions";
import type { GetAnswer } from "./api/client";
import styles from "./paused.module.css";

export type AllPaused = GetAnswer<"/paused">;

/** How the page reads and changes whether everything is paused, Dev flow's
 * API in the page and a test's own in a test. */
export type PausesApi = {
	loadPaused: () => Promise<AllPaused>;
	pauseAll: (paused: boolean) => Promise<void>;
};

export const PAUSED = ["devflow", "paused"] as const;

/** The strip's words. */
export const EVERYTHING_PAUSED =
	"Everything is paused. Nothing new starts in any project.";

/** Whether everything is paused, read as the page loads and when the window
 * regains focus, and again once the page changes it. */
export function usePaused(load: PausesApi["loadPaused"]) {
	return useQuery({
		queryKey: PAUSED,
		queryFn: load,
		refetchOnWindowFocus: true,
	});
}

/** The strip, in the attention style, while everything is paused; nothing
 * otherwise, before the answer or when it could not be read included. */
export function PausedStrip({
	paused,
	continueAll,
}: {
	paused: AllPaused | undefined;
	/** Continues everything, and reads the answer again. */
	continueAll: () => Promise<void>;
}) {
	const [busy, setBusy] = useState(false);
	const [failed, setFailed] = useState<string | null>(null);
	if (!paused?.paused) return null;
	return (
		<div className={styles.strip} role="status">
			<p className={styles.words}>{EVERYTHING_PAUSED}</p>
			<Button
				kind="secondary"
				disabled={busy}
				onClick={async () => {
					setBusy(true);
					setFailed(null);
					try {
						await continueAll();
					} catch (error) {
						setFailed(`Could not continue: ${describe(error)}`);
					} finally {
						setBusy(false);
					}
				}}
			>
				Continue everything
			</Button>
			{failed === null ? null : (
				<p className={styles.failed} role="alert">
					{failed}
				</p>
			)}
		</div>
	);
}
