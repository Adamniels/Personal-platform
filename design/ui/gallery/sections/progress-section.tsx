import { Progress } from "../../src";
import { Note, Row, Section } from "./section";

/** Each bar is named by what it shows, which is also how a test finds it. */
export function ProgressSection() {
	return (
		<Section title="Progress bar">
			<Row label="Progress">
				<Progress value={0} label="Progress" />
				<Progress value={62} label="Progress" />
				<Progress value={100} label="Progress" />
			</Row>
			<Row label="Budget">
				<Progress value={0} label="Budget" kind="budget" />
				<Progress value={62} label="Budget" kind="budget" />
				<Progress value={100} label="Budget" kind="budget" />
			</Row>
			<Note>
				Amber for progress and context used, info for budget used. Each bar is
				an image named by its label and its value.
			</Note>
		</Section>
	);
}
