// The memory centre's one way to call the brain. Every call goes to /brain/api/ with the session
// cookie, which the browser attaches on the platform's one origin. A 401 is a session to refresh,
// never a failure to show: the client posts once to the core's /auth/refresh and repeats the
// call, and if that also gets a 401 it sends the browser to the core's refresh page, which
// decides whether a login is needed. See "How a feature authenticates" in
// High-level/docs/feature-contract.md and "Refresh is always a POST" in
// Core/docs/core-architecture.md.
import type { paths } from "./schema";

const API = "/brain/api";
const REFRESH = "/auth/refresh";

/** What went wrong with a call, whatever the brain or the network answered.
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

/** The paths under /brain/api/ that answer a GET, without the prefix, from
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

type GetParameters<P extends GetPath> =
	Full<P> extends keyof paths
		? paths[Full<P>] extends { get: { parameters: infer Q } }
			? Q
			: never
		: never;

/** What fills a GET's address: `path` for each `{name}` in it, and `query`
 * for its query string, each typed from the schema. A value that is null or
 * undefined is left out of the query string. */
export type GetParams<P extends GetPath> = Pick<
	GetParameters<P>,
	("path" | "query") & keyof GetParameters<P>
>;

/** The parameters are optional where the schema requires none. */
type GetArguments<P extends GetPath> =
	object extends GetParams<P>
		? [params?: GetParams<P>]
		: [params: GetParams<P>];

export type StateChange = "POST" | "PUT" | "PATCH" | "DELETE";

export type Client = {
	/** A typed GET, its path, parameters and answer from the generated
	 * schema. */
	get<P extends GetPath>(
		path: P,
		...params: GetArguments<P>
	): Promise<GetAnswer<P>>;
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
					title: "The brain could not be reached",
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
		get<P extends GetPath>(path: P, ...[params]: GetArguments<P>) {
			// Checked against the schema at the call; the address reads it as plain
			// values.
			const given = params as Parameters<typeof address>[1];
			return call("GET", address(path, given)) as Promise<GetAnswer<P>>;
		},
		send<T>(method: StateChange, path: string, body?: unknown) {
			return call(method, path, body) as Promise<T>;
		},
	};
}

type Value = string | number | boolean;
type Values = Record<string, Value | readonly Value[] | null | undefined>;

/** The path with each `{name}` filled, encoded, and the query string the
 * values that are given make, a list as its name once a value. A `{name}`
 * with no value is an error, since the call would go to the wrong
 * address. */
function address(
	path: string,
	params: { path?: Values; query?: Values } | undefined,
): string {
	const filled = path.replace(/\{([^}]+)\}/g, (_, name: string) => {
		const value = params?.path?.[name];
		if (value === undefined || value === null) {
			throw new Error(`${path} needs a value for {${name}}`);
		}
		return encodeURIComponent(String(value));
	});
	const query = new URLSearchParams();
	for (const [name, value] of Object.entries(params?.query ?? {})) {
		if (value === undefined || value === null) continue;
		for (const each of Array.isArray(value) ? value : [value]) {
			query.append(name, String(each));
		}
	}
	const search = query.toString();
	return search === "" ? filled : `${filled}?${search}`;
}

/** A problem document's title and detail, FastAPI's `detail` (a string, or
 * a list of validation messages), or the status alone when the body says
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
		detail: detailOf(fields.detail),
	};
}

function detailOf(detail: unknown): string | null {
	if (typeof detail === "string") return detail;
	if (!Array.isArray(detail)) return null;
	const messages = detail.flatMap((item: unknown) =>
		typeof item === "object" &&
		item !== null &&
		"msg" in item &&
		typeof item.msg === "string"
			? [item.msg]
			: [],
	);
	return messages.length === 0 ? null : messages.join(". ");
}
