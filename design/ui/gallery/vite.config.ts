import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Run from design/ui by `npm run build:gallery`, so root is relative to it.
// The page loads /design/tokens.css and /design/fonts.css from the proxy at
// runtime, as every frontend does; they are not bundled.
export default defineConfig({
	root: "gallery",
	base: "/design/gallery/",
	plugins: [react()],
	build: {
		outDir: "dist",
		emptyOutDir: true,
	},
});
