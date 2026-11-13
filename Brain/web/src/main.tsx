import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./app";
import { Providers } from "./providers";

const root = document.getElementById("root");
if (!root) throw new Error("index.html has no #root element");

createRoot(root).render(
	<StrictMode>
		<Providers>
			<App />
		</Providers>
	</StrictMode>,
);
