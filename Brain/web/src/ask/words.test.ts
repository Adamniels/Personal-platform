import { describe, expect, test } from "vitest";
import { answerItem } from "../test/fake-brain";
import { supportLine } from "./words";

const NONE = {
	activity: 0,
	assertion: 0,
	decision: 0,
	engagement: 0,
	exposure: 0,
	judgment: 0,
};

describe("an item's support, as counts and the day it was last supported", () => {
	const today = new Date(2026, 10, 13, 12);

	test("this year's day without the year, another year's with it", () => {
		const item = answerItem({
			last_supported_at: new Date(2026, 8, 3, 12).toISOString(),
		});
		expect(supportLine(item, today)).toBe(
			"Supported by 1 entry, last on 3 Sep",
		);
		const older = answerItem({
			last_supported_at: new Date(2025, 0, 9, 12).toISOString(),
		});
		expect(supportLine(older, today)).toBe(
			"Supported by 1 entry, last on 9 Jan 2025",
		);
	});

	test("every kind of support counts, and what contradicts it is said", () => {
		const item = answerItem({
			last_supported_at: new Date(2026, 10, 12, 12).toISOString(),
			support: {
				supports: { ...NONE, assertion: 1, activity: 2 },
				contradicts: { ...NONE, judgment: 1 },
			},
		});
		expect(supportLine(item, today)).toBe(
			"Supported by 3 entries, last on 12 Nov, contradicted by 1 entry",
		);
	});
});
