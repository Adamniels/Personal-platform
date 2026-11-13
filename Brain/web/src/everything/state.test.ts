import { describe, expect, test } from "vitest";
import { stateOf } from "./state";

describe("an item's state is one of four words, each with its tone", () => {
	test("Active is done", () => {
		expect(
			stateOf({ status: "active", inactive_reason: null, stale: false }),
		).toEqual({ key: "active", word: "Active", tone: "done" });
	});

	test("Still true? is needs-you, for a stale claim about now", () => {
		expect(
			stateOf({ status: "active", inactive_reason: null, stale: true }),
		).toEqual({ key: "stale", word: "Still true?", tone: "needs-you" });
	});

	test("Replaced is idle", () => {
		expect(
			stateOf({
				status: "inactive",
				inactive_reason: "superseded",
				stale: false,
			}),
		).toEqual({ key: "replaced", word: "Replaced", tone: "idle" });
	});

	test("Retired is idle", () => {
		expect(
			stateOf({ status: "inactive", inactive_reason: "retired", stale: false }),
		).toEqual({ key: "retired", word: "Retired", tone: "idle" });
	});

	test("an inactive item is never Still true?, whatever its flag", () => {
		expect(
			stateOf({ status: "inactive", inactive_reason: "retired", stale: true })
				.word,
		).toBe("Retired");
	});
});
