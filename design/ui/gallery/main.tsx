import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { Gallery } from "./gallery";

const root = document.getElementById("root");
if (!root) throw new Error("index.html has no #root element");

createRoot(root).render(
	<StrictMode>
		<Gallery />
	</StrictMode>,
);
