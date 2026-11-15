import { Button, Card, Status } from "../../src";
import { Note, Row, Section } from "./section";
import styles from "./section.module.css";

/** A card in each form, named by what it shows, then three in a row whose
 * bodies differ in length, laid out by the card grid of section 2, so their
 * footers align. */
export function CardSection() {
	return (
		<Section title="Card" wide>
			<Row label="Kinds" stack>
				<div className={styles.grid}>
					<Card title="Plain">One key fact about the thing.</Card>
					<Card title="With a status" aside={<Status tone="done">Done</Status>}>
						Finished at 12:40.
					</Card>
					<Card
						title="With a footer"
						aside="14 due"
						footer={<Button>One action</Button>}
					>
						Body: one key fact, then at most three rows.
					</Card>
					<Card
						title="Needing you"
						aside={<Status tone="needs-you">Needs review</Status>}
						footer={<Button>Review</Button>}
						attention
					>
						Three decisions wait for you.
					</Card>
				</div>
			</Row>
			<Row label="Three in a row" stack>
				<div className={styles.grid}>
					<Card title="Review" aside="14 due" footer={<Button>Start</Button>}>
						Fourteen cards are due today.
					</Card>
					<Card title="Lessons" aside="2 new" footer={<Button>Open</Button>}>
						Two lessons were written from yesterday's session notes, on
						scheduling and on borrowing rules, and wait to be read before the
						next session starts.
					</Card>
					<Card title="Briefing" aside="08:00" footer={<Button>Listen</Button>}>
						Ready.
					</Card>
				</div>
			</Row>
			<Note>
				A card that needs the user gets the one accent border and says so to
				assistive technology. The footer is pushed to the bottom.
			</Note>
		</Section>
	);
}
