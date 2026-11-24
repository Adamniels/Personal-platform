import { VerdictTile, VerdictTiles } from "../../src";
import { Note, Row, Section } from "./section";

/** A row of three tiles in their tones, a tile without a line, and one
 * whose label and line are long enough to wrap. */
export function VerdictTileSection() {
	return (
		<Section title="Verdict tile" wide>
			<Row label="A row of three" stack>
				<VerdictTiles aria-label="A pull request at a glance">
					<VerdictTile
						label="The check"
						tone="done"
						status="Passed"
						line="npm run check · exit 0"
					/>
					<VerdictTile
						label="The review"
						tone="failed"
						status="Changes asked for"
						line="on a3f9c12 · 2 blocking"
					/>
					<VerdictTile
						label="Decisions"
						tone="needs-you"
						status="2 for you"
						line="+3 it did not need you for"
					/>
				</VerdictTiles>
			</Row>
			<Row label="Without a line, and long" stack>
				<VerdictTiles aria-label="A feature at a glance">
					<VerdictTile label="The review" tone="idle" status="Not reviewed" />
					<VerdictTile
						label="Why it waits, in the runner's own words"
						tone="running"
						status="Sent back"
						line="devflow/21-add-the-empty-state-to-the-habits-list"
					/>
				</VerdictTiles>
			</Row>
			<Note>
				As many tiles a row as fit at 180px each; on a phone they stack.
			</Note>
		</Section>
	);
}
