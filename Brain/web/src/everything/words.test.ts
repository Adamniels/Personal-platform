import { describe, expect, test } from "vitest";
import { verbWords } from "./words";

describe("an event's verb in plain words", () => {
	test("what the owner did, for a verb the memory centre recorded", () => {
		expect(verbWords("asserted", "brain")).toBe("You entered this");
		expect(verbWords("restored", "brain")).toBe("You brought this back");
	});

	test("the verb and the feature, for a verb a feature sent", () => {
		expect(verbWords("completed", "desk")).toBe("Completed in desk");
		// A verb the memory centre also records reads as the feature's.
		expect(verbWords("asserted", "desk")).toBe("Asserted in desk");
	});
});
