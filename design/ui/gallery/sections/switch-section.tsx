import { Switch } from "../../src";
import { Note, Section } from "./section";

/** Each switch is labelled with the state it shows. */
export function SwitchSection() {
	return (
		<Section title="Switch">
			<div>
				<Switch label="Off" />
				<Switch label="On" defaultChecked />
				<Switch
					label="With help"
					help="On: a card with questions waits for your answers. Off: it starts on the agent's recommendations."
				/>
				<Switch label="Disabled" disabled />
			</div>
			<Note>
				The whole row is the target: click the text to turn the switch on or
				off.
			</Note>
		</Section>
	);
}
