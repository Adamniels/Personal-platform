import { TextArea } from "../../src";
import { Note, Section } from "./section";

/** Each text area is labelled with the state it shows. */
export function TextAreaSection() {
	return (
		<Section title="Text area">
			<TextArea label="Empty" placeholder="Placeholder" rows={3} />
			<TextArea
				label="Filled"
				defaultValue={"First line of a note.\nA second line."}
				rows={3}
			/>
			<TextArea
				label="With help"
				placeholder="What happened"
				help="Help sits under the text area in caption style."
				rows={3}
			/>
			<TextArea label="With an error" error="the note is empty" rows={3} />
			<TextArea
				label="Disabled"
				defaultValue="Cannot be changed"
				rows={3}
				disabled
			/>
			<Note>A text area grows with its rows and can be resized taller.</Note>
		</Section>
	);
}
