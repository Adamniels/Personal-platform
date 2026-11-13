// A list of entries, each corrected in place, with its earlier versions and retiring, as "Editing
// changes provenance, and every edit records a judgment" in Brain/docs/trust-model.md has them.
// Nothing here deletes anything: a retired entry stays in Everything, where it can come back.
import { Button, Dialog, useToast } from "@platform/ui";
import { type ReactNode, useEffect, useId, useRef, useState } from "react";
import {
	type Entry,
	useEarlierVersions,
	useEditEntry,
	useRestoreEntry,
	useRetireEntry,
} from "./entries";
import styles from "./entries.module.css";
import { EntryForm } from "./entry-form";

/** What a list calls its entries. */
export type EntryLabels = {
	/** The text area's label when correcting one: "Entry". */
	field: string;
	/** What is said when one is saved with no text. */
	emptyError: string;
};

/** When a version was made, in the owner's time: "13 Nov 2026, 09:41". */
const WHEN = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
	year: "numeric",
	hour: "2-digit",
	minute: "2-digit",
});

export function formatWhen(iso: string): string {
	return WHEN.format(new Date(iso));
}

/** The entries in the order given, or what is shown when there are none. It
 * stays mounted when its last entry is retired, so that entry's Undo still
 * has somewhere to run. */
export function EntryList({
	entries,
	labels,
	empty,
	className,
}: {
	entries: readonly Entry[];
	labels: EntryLabels;
	/** Shown in place of the list when there are no entries. */
	empty: ReactNode;
	/** On the list itself, when there are entries. */
	className?: string;
}) {
	const retire = useRetireEntry();
	const restore = useRestoreEntry();
	const toast = useToast();

	async function retireEntry(entry: Entry) {
		await retire.mutateAsync(entry.id);
		toast.success("Entry retired", {
			label: "Undo",
			onAction: () => restore.mutate(entry.id),
		});
	}

	if (entries.length === 0) return empty;
	return (
		<ul className={[styles.list, className].filter(Boolean).join(" ")}>
			{entries.map((entry) => (
				<EntryRow
					// By lineage, so a correction, a new version, is the same row.
					key={entry.lineage_id}
					entry={entry}
					labels={labels}
					onRetire={retireEntry}
				/>
			))}
		</ul>
	);
}

function EntryRow({
	entry,
	labels,
	onRetire,
}: {
	entry: Entry;
	labels: EntryLabels;
	onRetire: (entry: Entry) => Promise<void>;
}) {
	const [editing, setEditing] = useState(false);
	// Set when the editor closes, so focus goes back to Edit rather than to
	// the page.
	const [refocus, setRefocus] = useState(false);
	const editButton = useRef<HTMLButtonElement>(null);
	const [showVersions, setShowVersions] = useState(false);
	const [confirming, setConfirming] = useState(false);
	const edit = useEditEntry();
	const toast = useToast();
	const textId = useId();
	const versionsId = useId();

	async function save(content: string) {
		// The same text again is no correction, so nothing is sent.
		if (content !== entry.content) {
			await edit.mutateAsync({ entry, content });
			toast.success("Entry saved");
		}
		stopEditing();
	}

	function stopEditing() {
		setEditing(false);
		setRefocus(true);
	}

	useEffect(() => {
		if (!refocus || editing) return;
		editButton.current?.focus();
		setRefocus(false);
	}, [refocus, editing]);

	if (editing) {
		return (
			<li className={styles.entry}>
				<EntryForm
					label={labels.field}
					initial={entry.content}
					emptyError={labels.emptyError}
					onSave={save}
					onCancel={stopEditing}
				/>
			</li>
		);
	}

	return (
		<li className={styles.entry}>
			<p id={textId} className={styles.text}>
				{entry.content}
			</p>
			<div className={styles.actions}>
				{entry.versions > 1 ? (
					<Button
						kind="ghost"
						aria-expanded={showVersions}
						aria-controls={showVersions ? versionsId : undefined}
						aria-describedby={textId}
						onClick={() => setShowVersions((shown) => !shown)}
					>
						Earlier versions
					</Button>
				) : null}
				<Button
					ref={editButton}
					kind="ghost"
					aria-describedby={textId}
					onClick={() => setEditing(true)}
				>
					Edit
				</Button>
				<Button
					kind="ghost"
					aria-describedby={textId}
					onClick={() => setConfirming(true)}
				>
					Retire
				</Button>
			</div>
			{showVersions ? <Versions id={versionsId} entry={entry} /> : null}
			{confirming ? (
				<Dialog
					open
					title="Retire this entry?"
					confirmLabel="Retire entry"
					kind="destructive"
					onConfirm={() =>
						onRetire(entry)
							// A failure has raised its toast already.
							.catch(() => undefined)
							.finally(() => setConfirming(false))
					}
					onCancel={() => setConfirming(false)}
				>
					It stops being used. It stays in Everything, where you can bring it
					back.
				</Dialog>
			) : null}
		</li>
	);
}

/** The entry's earlier versions, oldest first, each its text and when it was
 * made. */
function Versions({ id, entry }: { id: string; entry: Entry }) {
	const versions = useEarlierVersions(entry, true);

	if (versions.isPending) {
		return (
			<p id={id} className={styles.versionsNote}>
				Loading the earlier versions…
			</p>
		);
	}
	if (versions.isError) {
		return (
			<p id={id} className={styles.versionsNote}>
				The earlier versions could not be loaded.
			</p>
		);
	}
	return (
		<ol id={id} className={styles.versions} aria-label="Earlier versions">
			{versions.data.map((version) => (
				<li key={version.id} className={styles.version}>
					<p className={styles.versionText}>{version.content}</p>
					<time className={styles.when} dateTime={version.created_at}>
						{formatWhen(version.created_at)}
					</time>
				</li>
			))}
		</ol>
	);
}
