import { Chip } from "../../src";
import { Row, Section } from "./section";

export function ChipSection() {
	return (
		<Section title="Tool chip">
			<Row label="Tools">
				<Chip>Claude</Chip>
				<Chip>Bash</Chip>
			</Row>
		</Section>
	);
}
