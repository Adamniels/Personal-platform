import { Button, Card, NeedsYouBanner } from "../../src";
import { Note, Row, Section } from "./section";

/** The banner with its detail and its one action, with only its message,
 * and inside a card, which takes the accent border with it. */
export function NeedsYouBannerSection() {
	return (
		<Section title="Needs you banner">
			<Row label="With a detail and an action" stack>
				<NeedsYouBanner
					message="No worker yet, so no card can run."
					detail="Make the project's key, then start a worker with the two commands on its page."
					action={<Button>Set up a worker</Button>}
				/>
			</Row>
			<Row label="Its message alone" stack>
				<NeedsYouBanner message="The check of the setup failed." />
			</Row>
			<Row label="In a card" stack>
				<Card title="adamniels/personal-platform" attention>
					<NeedsYouBanner
						message="Setting up: 2 of 6 steps done."
						detail="No card runs until it is set up."
						action={<Button>Continue setting up</Button>}
					/>
				</Card>
			</Row>
			<Note>
				One per view, with at most one action. The card that holds it takes the
				accent border.
			</Note>
		</Section>
	);
}
