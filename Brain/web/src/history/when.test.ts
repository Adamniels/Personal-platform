import { describe, expect, test } from "vitest";
import { occurredAt, writtenWhen } from "./when";

describe("when something happened is written to its precision", () => {
	test("a year, a month and a day, as they were said", () => {
		expect(writtenWhen("2021-01-01T00:00:00Z", "year")).toBe("2021");
		expect(writtenWhen("2023-09-01T00:00:00Z", "month")).toBe("Sep 2023");
		expect(writtenWhen("2026-11-12T00:00:00Z", "day")).toBe("12 Nov 2026");
	});

	test("an instant is a day with a time, in the owner's time", () => {
		const at = new Date(2026, 10, 12, 14, 5);
		expect(writtenWhen(at.toISOString(), "instant")).toBe("12 Nov 2026, 14:05");
	});

	test("a year, a month or a day is read as said, whatever the owner's zone", () => {
		// The start of the period in UTC, the way it is sent, never the day
		// before where the owner is behind UTC.
		expect(writtenWhen("2021-01-01T00:00:00Z", "day")).toBe("1 Jan 2021");
		expect(writtenWhen("2024-12-31T00:00:00Z", "year")).toBe("2024");
	});
});

describe("what is said is sent as the start of its period", () => {
	test("a year, a month and a day", () => {
		expect(occurredAt({ precision: "year", year: "2021" })).toBe(
			"2021-01-01T00:00:00Z",
		);
		expect(occurredAt({ precision: "month", year: "2023", month: "9" })).toBe(
			"2023-09-01T00:00:00Z",
		);
		expect(occurredAt({ precision: "day", day: "2026-11-12" })).toBe(
			"2026-11-12T00:00:00Z",
		);
	});

	test("nothing for what is not a time", () => {
		expect(occurredAt({ precision: "year", year: "" })).toBeUndefined();
		expect(occurredAt({ precision: "year", year: "21" })).toBeUndefined();
		expect(occurredAt({ precision: "year", year: "0000" })).toBeUndefined();
		expect(
			occurredAt({ precision: "month", year: "2023", month: "" }),
		).toBeUndefined();
		expect(occurredAt({ precision: "day", day: "" })).toBeUndefined();
		expect(occurredAt({ precision: "day", day: "2026-02-30" })).toBeUndefined();
	});
});
