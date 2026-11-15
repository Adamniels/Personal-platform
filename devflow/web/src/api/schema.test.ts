// @vitest-environment node
/// <reference types="node" />
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test } from "vitest";

test("the generated types match the committed schema", () => {
	// The same script `npm run generate:api` writes the file with, printing
	// instead of writing.
	const generated = execFileSync(
		process.execPath,
		[
			fileURLToPath(
				new URL("../../tools/generate-schema.mjs", import.meta.url),
			),
		],
		{ encoding: "utf8" },
	);
	const committed = readFileSync(new URL("./schema.d.ts", import.meta.url), {
		encoding: "utf8",
	});

	expect(
		generated === committed,
		"src/api/schema.d.ts is stale: run npm run generate:api and review the diff",
	).toBe(true);
});
