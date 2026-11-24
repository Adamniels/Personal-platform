import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// The package's icons, lucide-react, sit in design/ui's node_modules beside its
// own React, for its tests. In a test here the "node" export condition gives
// them their CommonJS build, whose require("react") Node resolves from there, a
// second React that breaks hooks. So a test takes their ES module build instead
// and inlines it, which sends its React import through dedupe below, the one
// React the build uses too.
const icons = fileURLToPath(
	new URL(
		"../../design/ui/node_modules/lucide-react/dist/esm/lucide-react.mjs",
		import.meta.url,
	),
);

export default defineConfig({
	plugins: [react()],
	resolve: {
		dedupe: ["react", "react-dom"],
		alias: [{ find: /^lucide-react$/, replacement: icons }],
	},
	test: {
		environment: "jsdom",
		// Vitest replaces CSS it does not process with an empty string, ?raw
		// imports included, as design/ui's config says. The tests that check a
		// rule of a page's stylesheet read it as text.
		css: { include: [/\.css\?raw$/] },
		include: ["src/**/*.test.{ts,tsx}"],
		server: { deps: { inline: [/lucide-react/] } },
	},
});
