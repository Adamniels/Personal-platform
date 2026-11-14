// How the page says when a card comes back, always from the queue's own clock, its `as_of`, so the
// browser's clock never moves a step. A grade button's interval is rounded down; when the next card
// is due is counted up. The times themselves are the server's, never computed here. See "Studying"
// in review/README.md.
import { relativeDue } from "../cards/when";

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
/** A month here is 30 days, and a year 365. */
const MONTH = 30 * DAY;
const YEAR = 365 * DAY;

/** "now" under a minute, then "10m", "5h", "4d", "3mo" and "2y", each
 * rounded down. */
export function interval(asOf: string, preview: string): string {
	const ahead = Date.parse(preview) - Date.parse(asOf);
	if (ahead < MINUTE) return "now";
	if (ahead < HOUR) return `${Math.floor(ahead / MINUTE)}m`;
	if (ahead < DAY) return `${Math.floor(ahead / HOUR)}h`;
	if (ahead < MONTH) return `${Math.floor(ahead / DAY)}d`;
	if (ahead < YEAR) return `${Math.floor(ahead / MONTH)}mo`;
	return `${Math.floor(ahead / YEAR)}y`;
}

const RELATIVE = new Intl.RelativeTimeFormat("en", { numeric: "always" });

/** When the next card is due, as a sentence: "The next card is due in 3
 * days." Under an hour it counts the minutes up, so a card graded Again a
 * moment ago reads "in 1 minute", not "now", while nothing is due; beyond
 * that it is the Cards tab's relative time. */
export function nextDueSentence(nextDue: string, asOf: string): string {
	const ahead = Date.parse(nextDue) - Date.parse(asOf);
	const when =
		ahead <= 0
			? "now"
			: ahead < HOUR
				? RELATIVE.format(Math.ceil(ahead / MINUTE), "minute")
				: relativeDue(nextDue, Date.parse(asOf));
	return `The next card is due ${when}.`;
}
