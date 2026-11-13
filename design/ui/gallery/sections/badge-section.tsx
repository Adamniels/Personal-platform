import { Badge } from "../../src";
import { Row, Section } from "./section";

export function BadgeSection() {
	return (
		<Section title="Badge">
			<Row label="Categories">
				<Badge>Topic</Badge>
				<Badge>Rule</Badge>
			</Row>
			<Row label="Counts">
				<Badge>14</Badge>
			</Row>
		</Section>
	);
}
