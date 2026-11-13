// The Timeline section: every event the brain has recorded, newest recorded first, grouped under
// the day each was recorded, as "The shape" in Brain/docs/event-model.md has an event: what was
// done, its readable evidence, where it came from and when. The payload is for features and is not
// shown, and nothing here changes an event, since events are append only.
import { Badge, EmptyState, Timeline, TimelineEntry } from "@platform/ui";
import { useId } from "react";
import styles from "../entries/entries.module.css";
import section from "../entries/section.module.css";
import { BRAIN_ORIGIN, verbWords } from "../everything/words";
import { type Event, useEvents } from "./queries";
import layout from "./timeline.module.css";
import { dayHeading, dayKey, entryTime } from "./when";

export function TimelineSection() {
	const events = useEvents();

	if (events.isPending) {
		return <p className={styles.none}>Loading the timeline…</p>;
	}
	if (events.isError) {
		return (
			<EmptyState
				title="The timeline could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}
	if (events.data.length === 0) {
		return (
			<EmptyState
				title="Nothing recorded yet"
				detail="What you enter and correct shows here."
			/>
		);
	}
	return (
		<div className={section.section}>
			{byDay(events.data).map((day) => (
				<Day key={day.key} events={day.events} />
			))}
		</div>
	);
}

type DayOfEvents = { key: string; events: Event[] };

/** The events, already newest first, cut into runs recorded on one day. */
function byDay(events: Event[]): DayOfEvents[] {
	const days: DayOfEvents[] = [];
	for (const event of events) {
		const key = dayKey(event.recorded_at);
		const last = days.at(-1);
		if (last?.key === key) last.events.push(event);
		else days.push({ key, events: [event] });
	}
	return days;
}

/** One day: its heading, which names its list, and its events. */
function Day({ events }: { events: Event[] }) {
	const heading = useId();
	const [first] = events;
	if (!first) return null;
	return (
		<section className={layout.day} aria-labelledby={heading}>
			<h2 id={heading} className={layout.heading}>
				{dayHeading(first.recorded_at)}
			</h2>
			<Timeline aria-labelledby={heading} className={section.panel}>
				{events.map((event) => (
					<Entry key={event.id} event={event} />
				))}
			</Timeline>
		</section>
	);
}

/** An event: its time; what was done in plain words, then its evidence;
 * and where it came from, with its subject as a badge when it has one. */
function Entry({ event }: { event: Event }) {
	const subject = event.topic?.name ?? event.entity?.name;
	return (
		<TimelineEntry
			time={entryTime(event)}
			detail={
				<span className={layout.detail}>
					<span>{event.origin === BRAIN_ORIGIN ? "Memory" : event.origin}</span>
					{subject ? <Badge>{subject}</Badge> : null}
				</span>
			}
		>
			<span className={layout.verb}>{verbWords(event.verb, event.origin)}</span>{" "}
			<span className={layout.evidence}>{event.evidence}</span>
		</TimelineEntry>
	);
}
