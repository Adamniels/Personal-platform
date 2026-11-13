import { Timeline, TimelineEntry } from "../../src";
import { Note, Row, Section } from "./section";

/** Three entries, the first needing the user, the last drawing no line. */
export function TimelineSection() {
	return (
		<Section title="Timeline entry">
			<Row label="Three entries" stack>
				<Timeline aria-label="Recent">
					<TimelineEntry
						time="12 Nov 19:31"
						detail="From session notes. Waiting for your review."
						attention
					>
						New topic: rate monotonic scheduling
					</TimelineEntry>
					<TimelineEntry time="10 Nov 21:05">
						Finished lesson 3: borrowing rules
					</TimelineEntry>
					<TimelineEntry time="9 Nov 08:12" detail="Two sessions a week.">
						Started the course
					</TimelineEntry>
				</Timeline>
			</Row>
			<Note>
				An entry that needs the user has the filled amber dot and says so to
				assistive technology. The last entry draws no line.
			</Note>
		</Section>
	);
}
