// When a state was last confirmed, as the Now section says it: "Confirmed today", or the day and
// month, with the year only when it is not this one. In the owner's time, since "today" is his.

const DAY_MONTH = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
});
const DAY_MONTH_YEAR = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
	year: "numeric",
});

function sameDay(a: Date, b: Date): boolean {
	return (
		a.getFullYear() === b.getFullYear() &&
		a.getMonth() === b.getMonth() &&
		a.getDate() === b.getDate()
	);
}

/** "Confirmed today", "Confirmed 12 Nov", or "Confirmed 3 Aug 2025". */
export function confirmedLabel(at: Date, today: Date = new Date()): string {
	if (sameDay(at, today)) return "Confirmed today";
	const format =
		at.getFullYear() === today.getFullYear() ? DAY_MONTH : DAY_MONTH_YEAR;
	return `Confirmed ${format.format(at)}`;
}
