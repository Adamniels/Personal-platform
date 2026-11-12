import { Plus, Settings } from "lucide-react";
import { Button } from "../../src";
import { Note, Row, Section } from "./section";

export function ButtonSection() {
	return (
		<Section title="Button">
			<Row label="Kinds">
				<Button kind="primary">Primary</Button>
				<Button kind="secondary">Secondary</Button>
				<Button kind="ghost">Ghost</Button>
				<Button kind="destructive">Destructive</Button>
			</Row>
			<Row label="States">
				<Button disabled>Disabled</Button>
			</Row>
			<Row label="With an icon">
				<Button>
					<Plus size={16} aria-hidden="true" />
					Add
				</Button>
				<Button kind="ghost" aria-label="Settings">
					<Settings size={16} aria-hidden="true" />
				</Button>
			</Row>
			<Note>
				Hover fills secondary and ghost with surface-2. Tab to a button to see
				the focus ring. At most one primary per view.
			</Note>
		</Section>
	);
}
