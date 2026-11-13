// The Now section: what is going on now, the owner's active states, as "Current state" in
// Brain/docs/knowledge-model.md has it. A state is a claim about now, so one nothing has supported
// for sixty days asks whether it still holds, "Confidence, currency and status are three different
// things" in Brain/docs/trust-model.md; one press of Still true confirms it.
import { Button, EmptyState, Status, useToast } from "@platform/ui";
import { useState } from "react";
import styles from "../entries/entries.module.css";
import { EntryForm } from "../entries/entry-form";
import { EntryList } from "../entries/entry-list";
import section from "../entries/section.module.css";
import { SubjectBadges, SubjectFields } from "../subjects/subject-fields";
import { NO_SUBJECTS, subjectsOf } from "../subjects/subjects";
import { confirmedLabel } from "./confirmed";
import { type NowEntry, useAddState, useConfirm, useNow } from "./queries";

export const NOW_LINE =
	"What you are working on, what is blocking you, what is coming up. The brain asks again after sixty days.";

export function Now() {
	const now = useNow();
	const add = useAddState();
	const confirm = useConfirm();
	const toast = useToast();
	const [adding, setAdding] = useState(false);
	const [subjects, setSubjects] = useState(NO_SUBJECTS);

	function close() {
		setAdding(false);
		setSubjects(NO_SUBJECTS);
	}

	async function save(content: string) {
		await add.mutateAsync({ content, ...subjectsOf(subjects) });
		close();
		toast.success("Entry saved");
	}

	if (now.isPending) {
		return <p className={styles.none}>Loading what is going on…</p>;
	}
	if (now.isError) {
		return (
			<EmptyState
				title="What is going on now could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}

	return (
		<div className={section.section}>
			<div className={section.toolbar}>
				<p className={section.lede}>{NOW_LINE}</p>
				<Button kind="primary" onClick={() => setAdding(true)}>
					Add
				</Button>
			</div>
			{adding ? (
				<div className={`${section.panel} ${section.form}`}>
					<EntryForm
						name="Add"
						label="What is going on"
						emptyError="Write what is going on first"
						after={<SubjectFields value={subjects} onChange={setSubjects} />}
						onSave={save}
						onCancel={close}
					/>
				</div>
			) : null}
			<EntryList<NowEntry>
				entries={now.data}
				className={section.panel}
				labels={{ field: "Entry", emptyError: "Write the entry first" }}
				empty={
					<EmptyState
						title="Nothing going on yet"
						detail="Add what you are working on, so the platform answers with it in mind."
					/>
				}
				meta={(entry) => (
					<>
						<SubjectBadges topics={entry.topics} entities={entry.entities} />
						<time className={styles.when} dateTime={entry.lastSupportedAt}>
							{confirmedLabel(new Date(entry.lastSupportedAt))}
						</time>
						{entry.stale ? <Status tone="needs-you">Still true?</Status> : null}
					</>
				)}
				actions={(entry, focusEdit) =>
					entry.stale ? (
						<Button
							onClick={() =>
								confirm
									.mutateAsync(entry.id)
									.then(focusEdit)
									// A failure has raised its toast already.
									.catch(() => undefined)
							}
							disabled={confirm.isPending}
						>
							Still true
						</Button>
					) : null
				}
			/>
		</div>
	);
}
