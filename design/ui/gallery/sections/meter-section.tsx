import { Meter } from "../../src";
import { Note, Section } from "./section";

export function MeterSection() {
	return (
		<Section title="Meter">
			<Meter label="CTX" value={62} description="Context used" />
			<Meter label="BUD" value={30} kind="budget" description="Budget used" />
			<Note>
				A mono label, the bar and a mono value. The bar is what is read, so the
				two texts are not read twice.
			</Note>
		</Section>
	);
}
