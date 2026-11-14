import { describe, expect, it } from "vitest";
import { interval } from "./intervals";

const AS_OF = "2026-11-14T09:00:00.000Z";
const SECOND = 1000;
const MINUTE = 60 * SECOND;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** The preview `ms` after the queue's time. */
function ahead(ms: number): string {
	return new Date(Date.parse(AS_OF) + ms).toISOString();
}

describe("interval", () => {
	it.each([
		["now", 0],
		["now", 59 * SECOND],
		["1m", MINUTE],
		["10m", 10 * MINUTE],
		["5m", 5.5 * MINUTE],
		["59m", HOUR - SECOND],
		["1h", HOUR],
		["5h", 5 * HOUR + 59 * MINUTE],
		["23h", DAY - SECOND],
		["1d", DAY],
		["4d", 4 * DAY],
		["29d", 30 * DAY - SECOND],
		["1mo", 30 * DAY],
		["3mo", 95 * DAY],
		["12mo", 364 * DAY],
		["1y", 365 * DAY],
		["2y", 2 * 365 * DAY + 300 * DAY],
	])("writes %s for a preview that far ahead", (written, ms) => {
		expect(interval(AS_OF, ahead(ms))).toBe(written);
	});

	it("rounds down, never up", () => {
		expect(interval(AS_OF, ahead(10 * MINUTE - 1))).toBe("9m");
		expect(interval(AS_OF, ahead(4 * DAY - 1))).toBe("3d");
	});

	it("writes a time already past as now", () => {
		expect(interval(AS_OF, ahead(-5 * MINUTE))).toBe("now");
	});
});
