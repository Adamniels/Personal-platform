import { Status } from "../../src";
import { Note, Row, Section } from "./section";

/** The five tones, each with the design's first example word, then the same
 * tones with words a feature might choose. */
export function StatusSection() {
	return (
		<Section title="Status label">
			<Row label="Tones">
				<Status tone="running">Running</Status>
				<Status tone="needs-you">Needs you</Status>
				<Status tone="done">Done</Status>
				<Status tone="failed">Failed</Status>
				<Status tone="idle">Idle</Status>
			</Row>
			<Row label="A feature's own words">
				<Status tone="running">Working</Status>
				<Status tone="needs-you">3 overdue</Status>
				<Status tone="done">Confirmed</Status>
			</Row>
			<Note>
				The tone decides the colours and the caller decides the word. A status
				is always a dot plus a word.
			</Note>
		</Section>
	);
}
