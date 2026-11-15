import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Dev flow serves the build: index.html for /devflow and each section's address, and the files
// under /devflow/assets/. The page loads the design files and the nav bar from the proxy at
// runtime, as every frontend does; they are not bundled. See devflow/README.md.
export default defineConfig({
	base: "/devflow/",
	plugins: [react()],
	// @platform/ui is linked from design/ui, which has React in its own node_modules for its
	// tests. Two copies of React on one page break hooks.
	resolve: { dedupe: ["react", "react-dom"] },
	build: {
		outDir: "dist",
		emptyOutDir: true,
	},
});
