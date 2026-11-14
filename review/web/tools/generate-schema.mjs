// Generates the page's API types from the committed schemas, so the page is written against what
// it calls: review's own, into src/api/schema.d.ts, and the brain's, whose topics the page picks
// from, into src/api/brain-schema.d.ts. `npm run generate:api` writes both, and
// src/api/schema.test.ts runs it for each to check neither is stale. The memory centre's tool,
// written again here. See review/README.md.
//
// Run: node tools/generate-schema.mjs <review|brain> [--write]
//
// openapi-typescript builds its output with the TypeScript compiler's JavaScript API, which
// TypeScript 7 no longer exports, and it declares a peer of typescript ^5. The project type
// checks with TypeScript 7, so package.json installs 5.9.3 beside it under the name
// `typescript-5`, and this process alone resolves openapi-typescript's `typescript` to it.
// Remove the hook, the alias and the override once openapi-typescript works with TypeScript 7.
import { writeFileSync } from "node:fs";
import { registerHooks } from "node:module";

registerHooks({
	resolve(specifier, context, nextResolve) {
		return nextResolve(
			specifier === "typescript" ? "typescript-5" : specifier,
			context,
		);
	},
});

const { default: openapiTS, astToString } = await import("openapi-typescript");

const TARGETS = {
	review: { contract: "review", output: "schema.d.ts" },
	brain: { contract: "brain", output: "brain-schema.d.ts" },
};

const target = TARGETS[process.argv[2]];
if (!target) {
	process.stderr.write(
		"usage: node tools/generate-schema.mjs <review|brain> [--write]\n",
	);
	process.exit(2);
}

const SCHEMA = new URL(
	`../../../contracts/${target.contract}/openapi.json`,
	import.meta.url,
);
const OUTPUT = new URL(`../src/api/${target.output}`, import.meta.url);

const HEADER = `// Generated from contracts/${target.contract}/openapi.json by tools/generate-schema.mjs, with
// \`npm run generate:api\`. Do not edit: src/api/schema.test.ts fails when it is stale.

`;

const generated = HEADER + astToString(await openapiTS(SCHEMA));

if (process.argv.includes("--write")) {
	writeFileSync(OUTPUT, generated);
} else {
	process.stdout.write(generated);
}
