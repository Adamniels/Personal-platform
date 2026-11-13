// When something happened, to the precision it is known at, as "Timestamps" in
// Brain/docs/knowledge-model.md asks: "sometime in 2021" is said as a year and written as one,
// never as 1 January. A year, a month or a day is sent as the start of its period in UTC and read
// back in UTC, so it is the same period wherever the owner is; an instant is a moment, read in
// his time. The month names are written out here rather than taken from Intl, whose en-GB short
// September is "Sept" in newer ICU data.

export type Precision = "instant" | "day" | "month" | "year";

/** What a form holds for when: the precision chosen and its fields, a year
 * as typed, a month as 1 to 12, a day as an input of type date gives it. */
export type WhenDraft = {
	precision: "year" | "month" | "day";
	year?: string;
	month?: string;
	day?: string;
};

export const MONTHS = [
	"January",
	"February",
	"March",
	"April",
	"May",
	"June",
	"July",
	"August",
	"September",
	"October",
	"November",
	"December",
] as const;

function short(month: number): string {
	return (MONTHS[month] ?? "").slice(0, 3);
}

function pad(value: number): string {
	return String(value).padStart(2, "0");
}

/** "2021", "Sep 2023", "12 Nov 2026", or "12 Nov 2026, 14:05" for an
 * instant. */
export function writtenWhen(iso: string, precision: Precision): string {
	const at = new Date(iso);
	if (precision === "year") return String(at.getUTCFullYear());
	if (precision === "month") {
		return `${short(at.getUTCMonth())} ${at.getUTCFullYear()}`;
	}
	if (precision === "day") {
		return `${at.getUTCDate()} ${short(at.getUTCMonth())} ${at.getUTCFullYear()}`;
	}
	return `${at.getDate()} ${short(at.getMonth())} ${at.getFullYear()}, ${pad(at.getHours())}:${pad(at.getMinutes())}`;
}

const YEAR = /^\d{4}$/;
const DAY = /^(\d{4})-(\d{2})-(\d{2})$/;

/** The start of the period the draft says, as the brain takes it, or
 * undefined when it says no real time. */
export function occurredAt(draft: WhenDraft): string | undefined {
	if (draft.precision === "day") {
		const parts = DAY.exec(draft.day ?? "");
		if (!parts) return undefined;
		const [year, month, day] = parts.slice(1).map(Number) as [
			number,
			number,
			number,
		];
		const at = new Date(Date.UTC(year, month - 1, day));
		// A day the month does not have rolls over, so it is no day.
		if (at.getUTCMonth() !== month - 1 || at.getUTCDate() !== day) {
			return undefined;
		}
		return startOf(year, month, day);
	}
	const year = draft.year?.trim() ?? "";
	if (!YEAR.test(year) || Number(year) < 1) return undefined;
	if (draft.precision === "year") return startOf(Number(year), 1, 1);
	const month = Number(draft.month);
	if (!Number.isInteger(month) || month < 1 || month > 12) return undefined;
	return startOf(Number(year), month, 1);
}

function startOf(year: number, month: number, day: number): string {
	return `${String(year).padStart(4, "0")}-${pad(month)}-${pad(day)}T00:00:00Z`;
}

/** Today in the owner's time, as an input of type date holds it. */
export function today(now: Date = new Date()): string {
	return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
