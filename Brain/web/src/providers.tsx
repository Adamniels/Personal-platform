import {
	MutationCache,
	QueryCache,
	QueryClient,
	QueryClientProvider,
} from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { ToastProvider, useToast } from "./toast/toast";

/** What the application runs inside: toasts, and React Query for what the
 * server holds, so a change refreshes every view that shows it. */
export function Providers({ children }: { children: ReactNode }) {
	return (
		<ToastProvider>
			<Queries>{children}</Queries>
		</ToastProvider>
	);
}

function Queries({ children }: { children: ReactNode }) {
	const toast = useToast();
	// Every failed call raises an error toast, in one place. A 401 never gets
	// here: the client refreshes the session, or leaves the page for the core.
	const [client] = useState(
		() =>
			new QueryClient({
				queryCache: new QueryCache({
					onError: (error) => toast.error(error.message),
				}),
				mutationCache: new MutationCache({
					onError: (error) => toast.error(error.message),
				}),
				// A failure is shown at once rather than after three retries.
				defaultOptions: { queries: { retry: false } },
			}),
	);
	return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
