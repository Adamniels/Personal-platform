import { ToastProvider, useToast } from "@platform/ui";
import {
	MutationCache,
	QueryCache,
	QueryClient,
	QueryClientProvider,
} from "@tanstack/react-query";
import { createContext, type ReactNode, useContext, useState } from "react";
import { ApiError, type Client, createClient } from "./api/client";

declare module "@tanstack/react-query" {
	interface Register {
		mutationMeta: {
			/** The change shows a 422 on its form's field, so the toast leaves
			 * it out. Every other failure still raises one. */
			fieldErrors?: boolean;
		};
	}
}

const ClientContext = createContext<Client | null>(null);

/** The one client the application calls the brain with. */
export function useClient(): Client {
	const client = useContext(ClientContext);
	if (!client) throw new Error("useClient is used outside Providers");
	return client;
}

/** Whether a failed change is a refusal its form shows on the field. */
export function isFieldError(error: unknown): error is ApiError {
	return error instanceof ApiError && error.problem.status === 422;
}

/** What the application runs inside: toasts, the client, and React Query for
 * what the server holds, so a change refreshes every view that shows it. A
 * test passes a client over a fetch of its own. */
export function Providers({
	children,
	client,
}: {
	children: ReactNode;
	client?: Client;
}) {
	const [api] = useState(() => client ?? createClient());
	return (
		<ToastProvider>
			<ClientContext.Provider value={api}>
				<Queries>{children}</Queries>
			</ClientContext.Provider>
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
					onError: (error, _variables, _result, mutation) => {
						if (mutation.meta?.fieldErrors && isFieldError(error)) return;
						toast.error(error.message);
					},
				}),
				// A failure is shown at once rather than after three retries.
				defaultOptions: { queries: { retry: false } },
			}),
	);
	return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
