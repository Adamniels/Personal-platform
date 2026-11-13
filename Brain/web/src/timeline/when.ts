// When an event shows on the timeline: the day it was recorded, which it is grouped under, and its
// time, or both times when it happened on another day than it was recorded, as "The shape" in
// Brain/docs/event-model.md keeps them apart. Something from 2021 entered today should not read as
// today's news. Recorded is an instant, read in the owner's time; happened is read to its
// precision, as History writes it (src/history/when.ts).
import { dayOfInstant, type Precision, writtenWhen } from "../history/when";

/** The calendar day an instant falls on in the owner's time, as a key:
 * "2026-11-12". */
export function dayKey(iso: string): string {
	const at = new Date(iso);
	return localDay(at);
}

function localDay(at: Date): string {
	return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

function pad(value: number): string {
	return String(value).padStart(2, "0");
}

/** A day's heading: "Today", "Yesterday", then the date, "3 Oct 2026". */
export function dayHeading(iso: string, now: Date = new Date()): string {
	const day = dayKey(iso);
	if (day === localDay(now)) return "Today";
	const yesterday = new Date(now);
	yesterday.setDate(now.getDate() - 1);
	if (day === localDay(yesterday)) return "Yesterday";
	return dayOfInstant(iso);
}

type When = {
	occurred_at: string;
	occurred_precision: Precision;
	recorded_at: string;
};

/** The day something happened, to a day's precision, as a key, or undefined
 * when it is known only to a month or a year, so it falls on no one day. */
function occurredDay({ occurred_at, occurred_precision }: When) {
	if (occurred_precision === "instant") return dayKey(occurred_at);
	if (occurred_precision === "day") {
		// A day is the start of its period in UTC, so it is read in UTC.
		const at = new Date(occurred_at);
		return `${at.getUTCFullYear()}-${pad(at.getUTCMonth() + 1)}-${pad(at.getUTCDate())}`;
	}
	return undefined;
}

/** An entry's time: when it was recorded, "14:05", under its day's heading;
 * or, when it happened on another day, both, "Happened 2021 · Recorded 12
 * Nov 2026", happened to its precision and recorded to the day. */
export function entryTime(event: When): string {
	if (occurredDay(event) === dayKey(event.recorded_at)) {
		const at = new Date(event.recorded_at);
		return `${pad(at.getHours())}:${pad(at.getMinutes())}`;
	}
	const happened = writtenWhen(event.occurred_at, event.occurred_precision);
	return `Happened ${happened} · Recorded ${dayOfInstant(event.recorded_at)}`;
}
