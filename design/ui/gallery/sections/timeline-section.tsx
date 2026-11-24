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
			<Row label="With a title and a status label" stack>
				<Timeline aria-label="How it got here">
					<TimelineEntry
						time="20 Nov 14:02 · fodr-mini-1 · took 6 min 12 s"
						title="Build"
						status={{ tone: "done", word: "Succeeded" }}
					>
						Check exit 0 on 4c7d210
					</TimelineEntry>
					<TimelineEntry
						time="20 Nov 14:09"
						title="Review"
						status={{ tone: "failed", word: "Changes asked for" }}
					>
						The date goes through toISOString, so it ignores the locale.
					</TimelineEntry>
					<TimelineEntry
						time="Now"
						title="Now"
						status={{ tone: "needs-you", word: "Waiting for you" }}
						attention
					/>
				</Timeline>
			</Row>
			<Note>
				An entry that needs the user has the filled amber dot and says so to
				assistive technology. The last entry draws no line. With a title, the
				entry is named by it, its status label sits on the title's line, and its
				body holds blocks such as insets and lists.
			</Note>
		</Section>
	);
}
