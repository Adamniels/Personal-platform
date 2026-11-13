import { Checkbox } from "../../src";
import { Note, Section } from "./section";

/** Each checkbox is labelled with the state it shows. */
export function CheckboxSection() {
	return (
		<Section title="Checkbox">
			<div>
				<Checkbox label="Unchecked" />
				<Checkbox label="Checked" defaultChecked />
				<Checkbox label="Done" defaultChecked done />
				<Checkbox label="Disabled" disabled />
			</div>
			<Note>The whole row is the target: click the text to check the box.</Note>
		</Section>
	);
}
