// What the page runs inside: toasts, the two clients, and React Query for what review and the brain
// hold, so a change refreshes every view that shows it. The memory centre's
// Brain/web/src/providers.tsx, written again here, with the brain's client beside review's.
import { ToastProvider, useToast } from "@platform/ui";
import {
	MutationCache,
	QueryCache,
	QueryClient,
	QueryClientProvider,
} from "@tanstack/react-query";
import { createContext, type ReactNode, useContext, useState } from "react";
import {
	ApiError,
	type BrainClient,
	type Clients,
	createClients,
	type ReviewClient,
} from "./api/client";

declare module "@tanstack/react-query" {
	interface Register {
		queryMeta: {
			/** The view that reads the query says itself that it failed, so the
			 * toast leaves it out. */
			ownError?: boolean;
		};
		mutationMeta: {
			/** The change shows a 409 or a 422 on its form's field, so the
			 * toast leaves it out. Every other failure still raises one. */
			fieldErrors?: boolean;
			/** The change says a 409 itself, as a study session does when a
			 * card changed elsewhere, so the toast leaves it out. Every other
			 * failure still raises one. */
			conflicts?: boolean;
		};
	}
}

/** The root of every query over review's own data. Every change refreshes
 * everything under it. */
export const REVIEW = ["review"] as const;

const ClientsContext = createContext<Clients | null>(null);

/** The time, in milliseconds since the epoch, read where the page decides
 * something by it, so a test sets it rather than waiting. */
export type Clock = () => number;

const ClockContext = createContext<Clock>(Date.now);

/** The page's clock. */
export function useClock(): Clock {
	return useContext(ClockContext);
}

function useClients(): Clients {
	const clients = useContext(ClientsContext);
	if (!clients) throw new Error("a client is used outside Providers");
	return clients;
}

/** The client the page calls review with. */
export function useClient(): ReviewClient {
	return useClients().review;
}

/** The client the page calls the brain with, for its topics. */
export function useBrainClient(): BrainClient {
	return useClients().brain;
}

/** Whether a failed change is a refusal its form shows on the field: a 422,
 * what was entered breaks a rule, or a 409, it would be a second of
 * something unique, such as a deck's name. */
export function isFieldError(error: unknown): error is ApiError {
	return (
		error instanceof ApiError &&
		(error.problem.status === 422 || error.problem.status === 409)
	);
}

/** Whether a failed change was refused because what it was made from has
 * changed since, a 409. */
export function isConflict(error: unknown): error is ApiError {
	return error instanceof ApiError && error.problem.status === 409;
}

/** A test passes clients over a fetch of its own, and a clock. */
export function Providers({
	children,
	clients,
	now = Date.now,
}: {
	children: ReactNode;
	clients?: Clients;
	now?: Clock;
}) {
	const [api] = useState(() => clients ?? createClients());
	return (
		<ToastProvider>
			<ClockContext.Provider value={now}>
				<ClientsContext.Provider value={api}>
					<Queries>{children}</Queries>
				</ClientsContext.Provider>
			</ClockContext.Provider>
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
					onError: (error, query) => {
						if (query.meta?.ownError) return;
						toast.error(error.message);
					},
				}),
				mutationCache: new MutationCache({
					onError: (error, _variables, _result, mutation) => {
						if (mutation.meta?.fieldErrors && isFieldError(error)) return;
						if (mutation.meta?.conflicts && isConflict(error)) return;
						toast.error(error.message);
					},
				}),
				// A failure is shown at once rather than after three retries.
				defaultOptions: { queries: { retry: false } },
			}),
	);
	return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
