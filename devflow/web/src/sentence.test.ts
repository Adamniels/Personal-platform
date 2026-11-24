import { describe, expect, test } from "vitest";
import { sentence } from "./sentence";

describe("a sentence", () => {
	test("a clause gets a capital and a full stop", () => {
		expect(sentence("it reached the most turns, 200 turns")).toBe(
			"It reached the most turns, 200 turns.",
		);
	});

	test("a sentence that already ends stays as it is, trimmed", () => {
		expect(sentence(" Claude's API did not answer. ")).toBe(
			"Claude's API did not answer.",
		);
		expect(sentence("Did it?")).toBe("Did it?");
	});
});
