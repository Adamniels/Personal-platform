// The Timeline section: every event the brain has recorded, newest recorded first, grouped under
// the day each was recorded, as "The shape" in Brain/docs/event-model.md has an event: what was
// done, its readable evidence, where it came from and when. The payload is for features and is not
// shown, and nothing here changes an event, since events are append only.
import {
	Badge,
	Button,
	EmptyState,
	Timeline,
	TimelineEntry,
} from "@platform/ui";
import { useEffect, useId, useRef, useState } from "react";
import styles from "../entries/entries.module.css";
import section from "../entries/section.module.css";
import { itemPath } from "../everything/everything-paths";
import { BRAIN_ORIGIN, verbWords } from "../everything/words";
import { Link } from "../link";
import { type Event, useEvents } from "./queries";
import layout from "./timeline.module.css";
import { dayHeading, dayKey, entryTime } from "./when";

export function TimelineSection() {
	const events = useEvents();
	const list = useRef<HTMLDivElement>(null);
	// The first event of the page Show earlier last loaded, which takes focus
	// once it is drawn, so the place a keyboard or a screen reader was at
	// moves on to what is new, and the page does not scroll.
	const [landing, setLanding] = useState<string | null>(null);

	useEffect(() => {
		if (landing === null) return;
		list.current
			?.querySelector<HTMLElement>(`[data-event="${landing}"]`)
			?.focus({ preventScroll: true });
	}, [landing]);

	// A page of earlier events that fails keeps what is shown; its error is
	// a toast, as every failed query's is.
	if (events.data === undefined) {
		return events.isError ? (
			<EmptyState
				title="The timeline could not be loaded"
				detail="Reload the page to try again."
			/>
		) : (
			<p className={styles.none}>Loading the timeline…</p>
		);
	}
	const shown = events.data.pages.flat();
	if (shown.length === 0) {
		return (
			<EmptyState
				title="Nothing recorded yet"
				detail="What you enter and correct shows here."
			/>
		);
	}

	async function showEarlier() {
		const next = await events.fetchNextPage();
		const first = next.data?.pages.at(-1)?.[0];
		if (first) setLanding(first.id);
	}

	return (
		<div ref={list} className={section.section}>
			{byDay(shown).map((day) => (
				<Day key={day.key} events={day.events} />
			))}
			{events.hasNextPage ? (
				<div className={layout.foot}>
					<Button onClick={showEarlier} disabled={events.isFetchingNextPage}>
						Show earlier
					</Button>
				</div>
			) : null}
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

/** An event: its time; what was done in plain words, then its evidence,
 * a link to the item's page in Everything when it is about an item or made
 * one; and where it came from, with its subject as a badge when it has
 * one. */
function Entry({ event }: { event: Event }) {
	const subject = event.topic?.name ?? event.entity?.name;
	const item = event.subject_item ?? event.made_item;
	const line = (
		<>
			<span className={layout.verb}>{verbWords(event.verb, event.origin)}</span>{" "}
			<span className={layout.evidence}>{event.evidence}</span>
		</>
	);
	return (
		<TimelineEntry
			data-event={event.id}
			tabIndex={-1}
			className={layout.entry}
			time={entryTime(event)}
			detail={
				<span className={layout.detail}>
					<span>{event.origin === BRAIN_ORIGIN ? "Memory" : event.origin}</span>
					{subject ? <Badge>{subject}</Badge> : null}
				</span>
			}
		>
			{item ? (
				<Link className={layout.link} href={itemPath(item)}>
					{line}
				</Link>
			) : (
				line
			)}
		</TimelineEntry>
	);
}
