// The values file the platform serves at /design/tokens.css, read as text.

import { describe, expect, it } from "vitest";
import tokensCss from "../../tokens.css?raw";

type Declarations = Map<string, string>;

const withoutComments = tokensCss.replace(/\/\*[\s\S]*?\*\//g, "");

/** The body of the first rule whose selector is exactly `selector`, searched
 * from `from`. Rules in tokens.css hold no nested braces. */
function block(selector: string, from = 0): string {
	const start = withoutComments.indexOf(`${selector} {`, from);
	if (start < 0) throw new Error(`no rule for ${selector} in tokens.css`);
	const open = withoutComments.indexOf("{", start);
	const close = withoutComments.indexOf("}", open);
	return withoutComments.slice(open + 1, close);
}

function declarations(body: string): Declarations {
	const result: Declarations = new Map();
	for (const part of body.split(";")) {
		const colon = part.indexOf(":");
		if (colon < 0) continue;
		result.set(part.slice(0, colon).trim(), part.slice(colon + 1).trim());
	}
	return result;
}

const light = declarations(block(":root"));
const dark = declarations(block('[data-theme="dark"]'));
const mediaStart = withoutComments.indexOf(
	"@media (prefers-color-scheme: dark)",
);
const systemDark = declarations(
	block(':root:not([data-theme="light"])', mediaStart),
);

/** A theme is the light values with that theme's values on top, as the page
 * resolves them. */
const themes: Record<string, Declarations> = {
	light,
	dark: new Map([...light, ...dark]),
};

function rgb(hex: string): [number, number, number] {
	const match = /^#([0-9a-f]{6})$/i.exec(hex);
	if (!match?.[1]) throw new Error(`${hex} is not a six digit hex colour`);
	const n = Number.parseInt(match[1], 16);
	return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function luminance(hex: string): number {
	const [r, g, b] = rgb(hex).map((c) => {
		const s = c / 255;
		return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
	}) as [number, number, number];
	return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** The WCAG 2 contrast ratio between two colours. */
function contrast(a: string, b: string): number {
	const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [
		number,
		number,
	];
	return (hi + 0.05) / (lo + 0.05);
}

describe("the dark values are the same in both blocks", () => {
	it("finds the dark values", () => {
		expect(dark.size).toBeGreaterThan(10);
		expect(dark.get("--bg")).toBeDefined();
	});

	it("holds the same names with the same values", () => {
		expect(Object.fromEntries(systemDark)).toEqual(Object.fromEntries(dark));
	});
});

describe("every text colour has the contrast the design promises", () => {
	const pairs: [string, string][] = [];
	for (const text of ["--text", "--text-2", "--text-3"]) {
		for (const ground of ["--bg", "--surface", "--surface-2", "--surface-3"])
			pairs.push([text, ground]);
	}
	pairs.push(["--on-accent", "--accent"]);

	for (const [theme, values] of Object.entries(themes)) {
		it.each(pairs)(`${theme}: %s on %s reaches 4.5 to 1`, (text, ground) => {
			const fg = values.get(text);
			const bg = values.get(ground);
			if (!fg || !bg)
				throw new Error(`${text} or ${ground} missing in ${theme}`);

			expect(contrast(fg, bg)).toBeGreaterThanOrEqual(4.5);
		});
	}
});
