import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
	plugins: [react()],
	// The token tests read ../tokens.css with ?raw, which Vite refuses outside
	// the allowed folders. A single file cannot be allowed, since Vite checks the
	// id with its query, so the design folder is. This is the test runner's own
	// module loader; it serves nothing over the network.
	server: { fs: { allow: [".."] } },
	test: {
		environment: "jsdom",
		// Vitest replaces CSS it does not process with an empty string, ?raw
		// imports included. The tests that read a stylesheet as text need it.
		css: { include: [/\.css\?raw$/] },
		include: ["src/**/*.test.{ts,tsx}"],
	},
});
