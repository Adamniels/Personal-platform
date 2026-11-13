import type { ReactNode } from "react";
import { Select } from "../../src";
import { Note, Section } from "./section";

const options: ReactNode = (
	<>
		<option value="platform">Personal platform</option>
		<option value="wiki">Project wiki</option>
		<option value="studies">Studies</option>
	</>
);

/** Each select is labelled with the state it shows. */
export function SelectSection() {
	return (
		<Section title="Select">
			<Select label="Empty" defaultValue="">
				<option value="">Choose a project</option>
				{options}
			</Select>
			<Select label="Filled" defaultValue="wiki">
				{options}
			</Select>
			<Select
				label="With help"
				defaultValue="platform"
				help="Help sits under the select in caption style."
			>
				{options}
			</Select>
			<Select label="With an error" defaultValue="" error="choose a project">
				<option value="">Choose a project</option>
				{options}
			</Select>
			<Select label="Disabled" defaultValue="studies" disabled>
				{options}
			</Select>
			<Note>The menu is the browser's own.</Note>
		</Section>
	);
}
