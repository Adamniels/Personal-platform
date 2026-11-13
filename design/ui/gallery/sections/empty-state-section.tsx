import { Button, EmptyState } from "../../src";
import { Note, Row, Section } from "./section";

/** The empty state with its one action and without one. */
export function EmptyStateSection() {
	return (
		<Section title="Empty state">
			<Row label="With an action" stack>
				<EmptyState
					title="No cards due"
					detail="The next 6 cards come back tomorrow morning."
					action={<Button>Review ahead</Button>}
				/>
			</Row>
			<Row label="Without one" stack>
				<EmptyState
					title="Nothing in review"
					detail="New entries show up here when an agent writes one."
				/>
			</Row>
			<Note>At most one action, a secondary button. No illustration.</Note>
		</Section>
	);
}
