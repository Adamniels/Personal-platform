// The page's one way to call review. Every call goes to /review/api/ with the session cookie, which
// the browser attaches on the platform's one origin. A 401 is a session to refresh, never a
// failure to show: the client posts once to the core's /auth/refresh and repeats the call, and if
// that also gets a 401 it sends the browser to the core's refresh page, which decides whether a
// login is needed. See "How a feature authenticates" in High-level/docs/feature-contract.md and
// "Refresh is always a POST" in Core/docs/core-architecture.md. The memory centre's
// Brain/web/src/api/client.ts, written again here, since no service imports from another's
// directory.
import type { paths } from "./schema";

const API = "/review/api";
const REFRESH = "/auth/refresh";

/** What went wrong with a call, whatever review or the network answered.
 * `status` is 0 when no answer came. */
export type Problem = {
	status: number;
	title: string;
	detail: string | null;
};

export class ApiError extends Error {
	readonly problem: Problem;

	constructor(problem: Problem) {
		super(
			problem.detail === null
				? problem.title
				: `${problem.title}: ${problem.detail}`,
		);
		this.name = "ApiError";
		this.problem = problem;
	}
}

/** What the client needs from the browser, passed in so a test can be the
 * browser. */
export type ClientEnvironment = {
	fetch: (input: string, init: RequestInit) => Promise<Response>;
	location: Pick<Location, "pathname" | "search" | "assign">;
};

type Full<P extends string> = `${typeof API}${P}`;

/** The paths under /review/api/ that answer a GET, without the prefix, from
 * the schema the types were generated from. */
export type GetPath = {
	[P in keyof paths]: paths[P] extends { get: object }
		? P extends Full<infer Relative>
			? Relative
			: never
		: never;
}[keyof paths];

/** The JSON a GET on the path answers with a 200. */
export type GetAnswer<P extends GetPath> =
	Full<P> extends keyof paths
		? paths[Full<P>] extends {
				get: {
					responses: { 200: { content: { "application/json": infer T } } };
				};
			}
			? T
			: never
		: never;

export type StateChange = "POST" | "PUT" | "PATCH" | "DELETE";

export type Client = {
	/** A typed GET, its path and answer from the generated schema. */
	get<P extends GetPath>(path: P): Promise<GetAnswer<P>>;
	/** A call that changes state, its body sent as JSON. Resolves with the
	 * answer's JSON, or undefined for a 204. */
	send<T = unknown>(
		method: StateChange,
		path: string,
		body?: unknown,
	): Promise<T>;
};

const browser: ClientEnvironment = {
	// Called through window, which fetch needs as its receiver.
	fetch: (input, init) => window.fetch(input, init),
	location: window.location,
};

export function createClient(env: ClientEnvironment = browser): Client {
	// Calls that get a 401 together wait for the same refresh.
	let refreshing: Promise<void> | null = null;

	const here = () => env.location.pathname + env.location.search;

	function refresh(): Promise<void> {
		refreshing ??= env
			.fetch(REFRESH, {
				method: "POST",
				credentials: "same-origin",
				headers: { "content-type": "application/x-www-form-urlencoded" },
				body: new URLSearchParams({ return_to: here() }),
				// The core answers 303, back to the page: nothing to follow.
				redirect: "manual",
			})
			// Whatever the refresh answered, the repeated call says whether it
			// worked.
			.then(
				() => undefined,
				() => undefined,
			)
			.finally(() => {
				refreshing = null;
			});
		return refreshing;
	}

	async function call(
		method: string,
		path: string,
		body?: unknown,
	): Promise<unknown> {
		const init: RequestInit = {
			method,
			credentials: "same-origin",
			headers:
				body === undefined
					? { accept: "application/json" }
					: { accept: "application/json", "content-type": "application/json" },
			body: body === undefined ? undefined : JSON.stringify(body),
		};
		const attempt = () =>
			env.fetch(`${API}${path}`, init).catch(() => {
				throw new ApiError({
					status: 0,
					title: "Review could not be reached",
					detail: null,
				});
			});

		let response = await attempt();
		if (response.status === 401) {
			await refresh();
			response = await attempt();
			if (response.status === 401) {
				env.location.assign(
					`${REFRESH}?return_to=${encodeURIComponent(here())}`,
				);
				// The page is leaving. Settling would show an error on the way
				// out, so the call never does.
				return new Promise<never>(() => {});
			}
		}
		if (!response.ok) throw new ApiError(await readProblem(response));
		if (response.status === 204) return undefined;
		return response.json();
	}

	// The schema types the answer; nothing checks it at runtime.
	return {
		get<P extends GetPath>(path: P) {
			return call("GET", path) as Promise<GetAnswer<P>>;
		},
		send<T>(method: StateChange, path: string, body?: unknown) {
			return call(method, path, body) as Promise<T>;
		},
	};
}

/** A problem document's title and detail, RFC 9457 as ASP.NET Core's
 * AddProblemDetails writes it, or the status alone when the body says
 * nothing readable. */
async function readProblem(response: Response): Promise<Problem> {
	const body: unknown = await response.json().catch(() => null);
	const fields: Record<string, unknown> =
		typeof body === "object" && body !== null ? { ...body } : {};
	return {
		status: response.status,
		title:
			typeof fields.title === "string"
				? fields.title
				: `The request failed (${response.status})`,
		detail: typeof fields.detail === "string" ? fields.detail : null,
	};
}
