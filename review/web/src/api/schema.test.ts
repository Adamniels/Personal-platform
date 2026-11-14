// @vitest-environment node
/// <reference types="node" />
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test } from "vitest";

test.each([
	["review", "schema.d.ts"],
	["brain", "brain-schema.d.ts"],
])("the generated types match the committed %s schema", (target, file) => {
	// The same script `npm run generate:api` writes the file with, printing
	// instead of writing.
	const generated = execFileSync(
		process.execPath,
		[
			fileURLToPath(
				new URL("../../tools/generate-schema.mjs", import.meta.url),
			),
			target,
		],
		{ encoding: "utf8" },
	);
	const committed = readFileSync(new URL(`./${file}`, import.meta.url), {
		encoding: "utf8",
	});

	expect(
		generated === committed,
		`src/api/${file} is stale: run npm run generate:api and review the diff`,
	).toBe(true);
});
