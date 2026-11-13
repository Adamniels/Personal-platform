import { useState } from "react";
import { Button, Dialog } from "../../src";
import { Note, Row, Section } from "./section";

/** How long the archive pretends to take, so the disabled confirming button
 * can be seen. */
const ARCHIVE_MS = 800;

/** A dialog of each kind, each opened from a button. */
export function DialogSection() {
	const [open, setOpen] = useState<"delete" | "archive" | null>(null);
	const [result, setResult] = useState("Nothing confirmed yet.");
	const close = () => setOpen(null);

	return (
		<Section title="Dialog">
			<Row label="Destructive and primary">
				<Button onClick={() => setOpen("delete")}>Delete 3 entries…</Button>
				<Button onClick={() => setOpen("archive")}>Archive the project…</Button>
			</Row>
			<p aria-live="polite">{result}</p>
			<Note>
				Escape, Cancel and a click on the backdrop cancel. The archive takes a
				moment, and its button is disabled while it runs.
			</Note>
			<Dialog
				open={open === "delete"}
				title="Delete 3 entries?"
				confirmLabel="Delete entries"
				kind="destructive"
				onConfirm={() => {
					setResult("Deleted.");
					close();
				}}
				onCancel={close}
			>
				<p>
					They are removed from memory and from every card built on them. This
					cannot be undone.
				</p>
			</Dialog>
			<Dialog
				open={open === "archive"}
				title="Archive the project?"
				confirmLabel="Archive project"
				onConfirm={async () => {
					await new Promise((resolve) => setTimeout(resolve, ARCHIVE_MS));
					setResult("Archived.");
					close();
				}}
				onCancel={close}
			>
				<p>Its sessions stay, and it can be restored from settings.</p>
			</Dialog>
		</Section>
	);
}
