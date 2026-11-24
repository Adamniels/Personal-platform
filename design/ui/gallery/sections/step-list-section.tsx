import { Button, Status, StepList, StepListItem } from "../../src";
import { Note, Row, Section } from "./section";

/** Each state once: two done, the current one needing the user with its
 * content open, and two to come. */
export function StepListSection() {
	return (
		<Section title="Step list">
			<Row label="Five steps" stack>
				<StepList aria-label="Setting it up">
					<StepListItem state="done" title="The setup run">
						Pull request #7 merged: .devflow/ is on the default branch.
					</StepListItem>
					<StepListItem state="done" title="The recipe on the default branch">
						Dev flow has read it from the default branch.
					</StepListItem>
					<StepListItem
						state="current"
						title="The settings"
						aside={<Status tone="needs-you">Needs you</Status>}
					>
						<Button kind="primary">Accept</Button>
					</StepListItem>
					<StepListItem state="to-come" title="A worker on the recipe">
						A worker built from the recipe says hello.
					</StepListItem>
					<StepListItem state="to-come" title="The check at registration">
						A worker runs the project's check; once it passes, cards run.
					</StepListItem>
				</StepList>
			</Row>
			<Note>
				A check on ok-soft when done, the number on the accent when current, the
				number on surface-3 to come. The current step is the list's current step
				to assistive technology; the others say done or to come.
			</Note>
		</Section>
	);
}
