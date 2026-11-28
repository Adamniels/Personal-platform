// "Run now anyway": the owner runs a feature, or a card on its own, outside its project's
// schedules until it has nothing left to run, and "Back to the schedule" takes that back. Offered
// on a feature's page and a card's page only while the project is outside its schedules and the
// thing has something to run; once on, its line says so. A refusal is shown in words, and what
// shows it is read again. See "Schedules" under "Jobs" in devflow/README.md.
import { Button, useToast } from "@platform/ui";
import { useState } from "react";
import { describe } from "./actions";
import type { components } from "./api/schema";
import styles from "./run-now.module.css";

/** What an override is for: a feature, by its parent's number, or a card on
 * its own. */
export type RunNowKind = "features" | "cards";

/** What a page says of "Run now anyway", as Dev flow answers it. */
export type RunNowState = components["schemas"]["RunNowState"];

/** How a feature or a card is run now, and taken back, Dev flow's API in
 * the page and a test's own in a test. */
export type RunNowApi = {
	/** Runs it outside its project's schedules until it has nothing left. */
	runNow: (
		projectId: string,
		kind: RunNowKind,
		number: number,
	) => Promise<void>;
	/** It keeps to its project's schedules again. */
	backToSchedule: (
		projectId: string,
		kind: RunNowKind,
		number: number,
	) => Promise<void>;
};

export const RUN_NOW = "Run now anyway";
export const RUN_NOW_WHY =
	"Runs this outside the schedule until it has nothing left to run.";
export const RUNNING_NOW =
	"Running outside the schedule, until it has nothing left to run";
export const BACK = "Back to the schedule";

/** What the toast says once `number` runs outside the schedule. */
export function runningOutside(number: number): string {
	return `Running #${number} outside the schedule`;
}

export type RunNowAnywayProps = {
	projectId: string;
	kind: RunNowKind;
	number: number;
	/** What the page says of it; nothing is shown without it. */
	state: RunNowState | null | undefined;
	api: RunNowApi;
	/** Reads again what shows it. */
	reload: () => Promise<unknown>;
};

/** On a feature's or a card's page: once on, its line and a ghost "Back to
 * the schedule"; outside the schedule with something to run, a secondary
 * "Run now anyway" and what it does; otherwise nothing. */
export function RunNowAnyway({
	projectId,
	kind,
	number,
	state,
	api,
	reload,
}: RunNowAnywayProps) {
	const toast = useToast();
	const [pressed, setPressed] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);

	if (!state) return null;
	const offered = !state.on && state.outside_schedule && state.something_to_run;
	if (!state.on && !offered) return null;

	async function press(on: boolean) {
		setPressed(true);
		try {
			if (on) {
				await api.runNow(projectId, kind, number);
				toast.success(runningOutside(number));
			} else {
				await api.backToSchedule(projectId, kind, number);
			}
			setRefusal(null);
		} catch (error) {
			setRefusal(describe(error));
		} finally {
			setPressed(false);
		}
		await reload();
	}

	return (
		<div className={styles.runNow}>
			{refusal === null ? null : (
				<p className={styles.refusal} role="alert">
					{refusal}
				</p>
			)}
			{state.on ? (
				<div className={styles.row}>
					<p className={styles.line}>{RUNNING_NOW}</p>
					<Button kind="ghost" disabled={pressed} onClick={() => press(false)}>
						{BACK}
					</Button>
				</div>
			) : (
				<div className={styles.row}>
					<Button
						kind="secondary"
						disabled={pressed}
						onClick={() => press(true)}
					>
						{RUN_NOW}
					</Button>
					<p className={styles.why}>{RUN_NOW_WHY}</p>
				</div>
			)}
		</div>
	);
}
