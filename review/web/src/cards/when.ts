// How the Cards tab writes a card: the first line of its front, cut, and when it is next due, as a
// relative time with the exact one beside it. The time is passed in, so a test sets it.

/** The most of a front the list shows. */
const FRONT_SHOWN = 120;

/** A front's first line, cut at 120 characters with an ellipsis. */
export function firstLine(front: string): string {
	const line = front.split("\n", 1)[0] ?? "";
	return line.length > FRONT_SHOWN ? `${line.slice(0, FRONT_SHOWN)}…` : line;
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

const RELATIVE = new Intl.RelativeTimeFormat("en", { numeric: "always" });

/** When a card is next due, from now: "in 3 days", "in 5 hours", or "now"
 * once it is due, a card overdue being due now. */
export function relativeDue(due: string, now: number): string {
	const ahead = Date.parse(due) - now;
	if (ahead < MINUTE) return "now";
	if (ahead < HOUR)
		return RELATIVE.format(Math.round(ahead / MINUTE), "minute");
	if (ahead < DAY) return RELATIVE.format(Math.round(ahead / HOUR), "hour");
	if (ahead < 30 * DAY) return RELATIVE.format(Math.round(ahead / DAY), "day");
	if (ahead < 365 * DAY) {
		return RELATIVE.format(Math.round(ahead / (30 * DAY)), "month");
	}
	return RELATIVE.format(Math.round(ahead / (365 * DAY)), "year");
}

/** The exact time, in the owner's time zone: "17 Nov 2026, 09:00". */
const EXACT = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
	year: "numeric",
	hour: "2-digit",
	minute: "2-digit",
});

export function exactTime(iso: string): string {
	return EXACT.format(new Date(iso));
}
