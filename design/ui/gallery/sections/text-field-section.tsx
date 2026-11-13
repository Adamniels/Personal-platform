import { TextField } from "../../src";
import { Note, Section } from "./section";

/** Each field is labelled with the state it shows. */
export function TextFieldSection() {
	return (
		<Section title="Text field">
			<TextField label="Empty" placeholder="Placeholder" />
			<TextField label="Filled" defaultValue="Write the slice" />
			<TextField
				label="With help"
				placeholder="you@example.com"
				help="Help sits under the field in caption style."
			/>
			<TextField
				label="With an error"
				defaultValue="hunter2"
				error="use at least 12 characters"
			/>
			<TextField label="Disabled" defaultValue="Cannot be changed" disabled />
			<Note>
				Tab to a field to see the focus ring. On phone the text is 16px and the
				field 44px tall.
			</Note>
		</Section>
	);
}
