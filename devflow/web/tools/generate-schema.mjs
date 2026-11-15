// Generates Dev flow's API types from the committed schema, so the page is written against
// what Dev flow serves. `npm run generate:api` writes src/api/schema.d.ts with it, and
// src/api/schema.test.ts runs it to check that file is not stale. The memory centre's tool, written
// again here, as review's is. See devflow/README.md.
//
// Run: node tools/generate-schema.mjs [--write]
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

const SCHEMA = new URL(
	"../../../contracts/devflow/openapi.json",
	import.meta.url,
);
const OUTPUT = new URL("../src/api/schema.d.ts", import.meta.url);

const HEADER = `// Generated from contracts/devflow/openapi.json by tools/generate-schema.mjs, with
// \`npm run generate:api\`. Do not edit: src/api/schema.test.ts fails when it is stale.

`;

const generated = HEADER + astToString(await openapiTS(SCHEMA));

if (process.argv.includes("--write")) {
	writeFileSync(OUTPUT, generated);
} else {
	process.stdout.write(generated);
}
