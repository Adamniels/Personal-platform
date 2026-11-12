import { describe, expect, it } from "vitest";

// Every component stylesheet, and the gallery's, as text.
const sheets = {
	...import.meta.glob<string>("./**/*.module.css", {
		query: "?raw",
		import: "default",
		eager: true,
	}),
	...import.meta.glob<string>("../gallery/**/*.module.css", {
		query: "?raw",
		import: "default",
		eager: true,
	}),
};

const rawColour = /#[0-9a-f]{3,8}\b|\brgba?\(|\bhsla?\(/i;

describe("no component style holds a raw colour", () => {
	it("finds the component styles", () => {
		expect(Object.keys(sheets)).toContain("./button/button.module.css");
	});

	it.each(Object.entries(sheets))(
		"%s uses only var(--…) colours",
		(_path, css) => {
			const offending = css.split("\n").filter((line) => rawColour.test(line));
			expect(offending).toEqual([]);
		},
	);
});
