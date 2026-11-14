// How a grade button says when the card would come back: the time from the queue's own clock, its
// `as_of`, to the preview, rounded down, so the browser's clock never moves a step. The preview
// itself is the server's, never computed here. See "Studying" in review/README.md.

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
