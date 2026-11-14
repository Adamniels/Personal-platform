// The page's one way to call a service: review's own API, and the brain's, whose topics the page
// picks from. Each is an instance of the same client over its generated schema, rooted at its
// service's /api/ path. Every call goes there with the session cookie, which the browser attaches
// on the platform's one origin, so a call to the brain is the owner holding his token, not review
// calling another feature, see "Display and navigation are composed in the browser" in
// High-level/docs/feature-contract.md.
//
// A 401 is a session to refresh, never a failure to show: the client posts once to the core's
// /auth/refresh and repeats the call, and if that also gets a 401 it sends the browser to the
// core's refresh page, which decides whether a login is needed. The two instances share one
// refresh, so calls to either that get a 401 together wait for the same one. See "How a feature
// authenticates" in High-level/docs/feature-contract.md and "Refresh is always a POST" in
// Core/docs/core-architecture.md. The memory centre's Brain/web/src/api/client.ts, written again
// here, since no service imports from another's directory.
import type { paths as BrainPaths } from "./brain-schema";
import type { paths as ReviewPaths } from "./schema";

const REFRESH = "/auth/refresh";

/** What went wrong with a call, whatever the service or the network
 * answered. `status` is 0 when no answer came. `errors` names each field a
 * 422 refused, as it is on the wire, such as `front` or `topics[1].slug`,
 * and is empty when the answer named none. */
export type Problem = {
	status: number;
	title: string;
	detail: string | null;
	errors: Record<string, readonly string[]>;
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

/** The paths under a root that answer a GET, without the root, from the
 * schema the types were generated from. */
export type GetPath<Paths, Root extends string> = {
	[P in keyof Paths]: Paths[P] extends { get: object }
		? P extends `${Root}${infer Relative}`
			? Relative
			: never
		: never;
}[keyof Paths];

type Operation<
	Paths,
	Root extends string,
	P extends string,
> = `${Root}${P}` extends keyof Paths
	? Paths[`${Root}${P}`] extends { get: infer G }
		? G
		: never
	: never;

/** The JSON a GET on the path answers with a 200. */
export type GetAnswer<Paths, Root extends string, P extends string> =
	Operation<Paths, Root, P> extends {
		responses: { 200: { content: { "application/json": infer T } } };
	}
		? T
		: never;

type GetParameters<Paths, Root extends string, P extends string> =
	Operation<Paths, Root, P> extends { parameters: infer Q } ? Q : never;

/** What fills a GET's address: `path` for each `{name}` in it, and `query`
 * for its query string, each typed from the schema. A value that is null or
 * undefined is left out of the query string. */
export type GetParams<Paths, Root extends string, P extends string> = Pick<
	GetParameters<Paths, Root, P>,
	("path" | "query") & keyof GetParameters<Paths, Root, P>
>;

/** The parameters are optional where the schema requires none. */
type GetArguments<Paths, Root extends string, P extends string> =
	object extends GetParams<Paths, Root, P>
		? [params?: GetParams<Paths, Root, P>]
		: [params: GetParams<Paths, Root, P>];

export type StateChange = "POST" | "PUT" | "PATCH" | "DELETE";

export type Client<Paths, Root extends string> = {
	/** A typed GET, its path, parameters and answer from the generated
	 * schema. */
	get<P extends GetPath<Paths, Root>>(
		path: P,
		...params: GetArguments<Paths, Root, P>
	): Promise<GetAnswer<Paths, Root, P>>;
	/** A call that changes state, its body sent as JSON. Resolves with the
	 * answer's JSON, or undefined for a 204. */
	send<T = unknown>(
		method: StateChange,
		path: string,
		body?: unknown,
	): Promise<T>;
};

export type ReviewClient = Client<ReviewPaths, "/review/api">;
export type BrainClient = Client<BrainPaths, "/brain/api">;

/** The page's two clients, sharing one refresh. */
export type Clients = { review: ReviewClient; brain: BrainClient };

const browser: ClientEnvironment = {
	// Called through window, which fetch needs as its receiver.
	fetch: (input, init) => window.fetch(input, init),
	location: window.location,
};

/** Review's client and the brain's, over one browser and one refresh. */
export function createClients(env: ClientEnvironment = browser): Clients {
	const session = createSession(env);
	return {
		review: createClient<ReviewPaths, "/review/api">(session, {
			root: "/review/api",
			unreachable: "Review could not be reached",
		}),
		brain: createClient<BrainPaths, "/brain/api">(session, {
			root: "/brain/api",
			unreachable: "The brain could not be reached",
		}),
	};
}

/** The browser, and the one refresh every client waits on. */
type Session = {
	env: ClientEnvironment;
	refresh: () => Promise<void>;
	here: () => string;
};

function createSession(env: ClientEnvironment): Session {
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

	return { env, refresh, here };
}

function createClient<Paths, Root extends string>(
	{ env, refresh, here }: Session,
	{ root, unreachable }: { root: Root; unreachable: string },
): Client<Paths, Root> {
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
			env.fetch(`${root}${path}`, init).catch(() => {
				throw new ApiError({
					status: 0,
					title: unreachable,
					detail: null,
					errors: {},
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
		get<P extends GetPath<Paths, Root>>(
			path: P,
			...[params]: GetArguments<Paths, Root, P>
		) {
			// Checked against the schema at the call; the address reads it as plain
			// values.
			const given = params as Parameters<typeof address>[1];
			return call("GET", address(path, given)) as Promise<
				GetAnswer<Paths, Root, P>
			>;
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

/** A problem document's title, detail and field errors, RFC 9457 as ASP.NET
 * Core's AddProblemDetails writes it for review, or FastAPI's `detail` for
 * the brain (a string, or a list of validation messages), or the status
 * alone when the body says nothing readable. */
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
		errors: errorsOf(fields.errors),
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

/** ASP.NET Core's `errors`, each field's messages, keeping only what is
 * text. */
function errorsOf(errors: unknown): Record<string, readonly string[]> {
	if (typeof errors !== "object" || errors === null) return {};
	const named: Record<string, readonly string[]> = {};
	for (const [field, messages] of Object.entries(errors)) {
		if (!Array.isArray(messages)) continue;
		const texts = messages.filter(
			(message): message is string => typeof message === "string",
		);
		if (texts.length > 0) named[field] = texts;
	}
	return named;
}
