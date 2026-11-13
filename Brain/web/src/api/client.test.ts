import { describe, expect, test, vi } from "vitest";
import { ApiError, type ClientEnvironment, createClient } from "./client";

type Call = { url: string; init: RequestInit };

/** A fetch that answers from a queue, in order, and records every call. A
 * function in the queue throws instead, as fetch does on a network error. */
function fakeFetch(answers: Array<Response | (() => never)>) {
	const calls: Call[] = [];
	const fetch = async (input: RequestInfo | URL, init: RequestInit = {}) => {
		calls.push({ url: String(input), init });
		const answer = answers.shift();
		if (answer === undefined) throw new Error(`unexpected call to ${input}`);
		if (typeof answer === "function") answer();
		return answer as Response;
	};
	return { fetch, calls };
}

function json(status: number, body: unknown, type = "application/json") {
	return new Response(JSON.stringify(body), {
		status,
		headers: { "content-type": type },
	});
}

const empty = (status: number) => new Response(null, { status });

function environment(fetch: ClientEnvironment["fetch"]) {
	return {
		fetch,
		location: {
			pathname: "/brain/knowledge",
			search: "?q=a b",
			assign: vi.fn(),
		},
	};
}

describe("the client refreshes once on a 401 and retries", () => {
	test("a 401, a refresh, then the request again", async () => {
		const { fetch, calls } = fakeFetch([
			empty(401),
			// The core answers the refresh with a 303, which a manual redirect
			// hides; the client reads nothing from it.
			empty(200),
			json(200, { account_id: "a" }),
		]);
		const env = environment(fetch);

		const answer = await createClient(env).get("/whoami");

		expect(answer).toEqual({ account_id: "a" });
		expect(calls.map((call) => call.url)).toEqual([
			"/brain/api/whoami",
			"/auth/refresh",
			"/brain/api/whoami",
		]);
		const refresh = calls[1]?.init;
		expect(refresh?.method).toBe("POST");
		expect(refresh?.redirect).toBe("manual");
		expect(refresh?.credentials).toBe("same-origin");
		expect(new Headers(refresh?.headers).get("content-type")).toBe(
			"application/x-www-form-urlencoded",
		);
		expect(String(refresh?.body)).toBe(
			"return_to=%2Fbrain%2Fknowledge%3Fq%3Da+b",
		);
		expect(env.location.assign).not.toHaveBeenCalled();
	});

	test("a second 401 sends the browser to the core's refresh page", async () => {
		const { fetch, calls } = fakeFetch([empty(401), empty(200), empty(401)]);
		const env = environment(fetch);
		let settled = false;

		void createClient(env)
			.get("/whoami")
			.finally(() => {
				settled = true;
			});

		await vi.waitFor(() =>
			expect(env.location.assign).toHaveBeenCalledWith(
				"/auth/refresh?return_to=%2Fbrain%2Fknowledge%3Fq%3Da%20b",
			),
		);
		// One refresh, never a loop.
		expect(calls.filter((call) => call.url === "/auth/refresh")).toHaveLength(
			1,
		);
		// The page is leaving: the call neither answers nor fails, so nothing
		// shows an error on the way out.
		await new Promise((resolve) => setTimeout(resolve, 10));
		expect(settled).toBe(false);
	});

	test("calls that get a 401 together share one refresh", async () => {
		const { fetch, calls } = fakeFetch([
			empty(401),
			empty(401),
			empty(200),
			json(200, { account_id: "a" }),
			json(200, { account_id: "a" }),
		]);
		const client = createClient(environment(fetch));

		await Promise.all([client.get("/whoami"), client.get("/whoami")]);

		expect(calls.filter((call) => call.url === "/auth/refresh")).toHaveLength(
			1,
		);
	});
});

describe("the client sends a state changing request as JSON and reads errors as typed problems", () => {
	test("the body is JSON, with the session cookie", async () => {
		const { fetch, calls } = fakeFetch([json(201, { id: "x" })]);

		const answer = await createClient(environment(fetch)).send(
			"POST",
			"/items",
			{ text: "Prefers tea" },
		);

		expect(answer).toEqual({ id: "x" });
		const call = calls[0];
		expect(call?.url).toBe("/brain/api/items");
		expect(call?.init.method).toBe("POST");
		expect(call?.init.credentials).toBe("same-origin");
		expect(new Headers(call?.init.headers).get("content-type")).toBe(
			"application/json",
		);
		expect(call?.init.body).toBe('{"text":"Prefers tea"}');
	});

	test("a 204 answers nothing", async () => {
		const { fetch } = fakeFetch([empty(204)]);

		const answer = await createClient(environment(fetch)).send(
			"DELETE",
			"/items/x",
		);

		expect(answer).toBeUndefined();
	});

	async function problemOf(answer: Response | (() => never)) {
		const { fetch } = fakeFetch([answer]);
		const error = await createClient(environment(fetch))
			.send("POST", "/items", {})
			.catch((caught: unknown) => caught);
		expect(error).toBeInstanceOf(ApiError);
		return (error as ApiError).problem;
	}

	test("a problem document is read as it is", async () => {
		const problem = await problemOf(
			json(
				409,
				{ title: "Already retired", detail: "The item was retired before." },
				"application/problem+json",
			),
		);

		expect(problem).toEqual({
			status: 409,
			title: "Already retired",
			detail: "The item was retired before.",
		});
	});

	test("FastAPI's detail, a string or a list of messages, is the detail", async () => {
		expect(await problemOf(json(404, { detail: "No such item" }))).toEqual({
			status: 404,
			title: "The request failed (404)",
			detail: "No such item",
		});
		expect(
			await problemOf(
				json(422, {
					detail: [{ msg: "Field required" }, { msg: "Too long" }],
				}),
			),
		).toEqual({
			status: 422,
			title: "The request failed (422)",
			detail: "Field required. Too long",
		});
	});

	test("a body that is not JSON, or no answer at all, is still a problem", async () => {
		expect(
			await problemOf(
				new Response("<html>Bad gateway</html>", { status: 502 }),
			),
		).toEqual({
			status: 502,
			title: "The request failed (502)",
			detail: null,
		});
		expect(
			await problemOf(() => {
				throw new TypeError("Failed to fetch");
			}),
		).toEqual({
			status: 0,
			title: "The brain could not be reached",
			detail: null,
		});
	});
});

describe("a GET's parameters fill its address", () => {
	test("a query string from the values given, the empty ones left out", async () => {
		const { fetch, calls } = fakeFetch([json(200, [])]);

		await createClient(environment(fetch)).get("/items", {
			query: { kind: "rule", status: null, topic: undefined },
		});

		expect(calls.map(({ url }) => url)).toEqual(["/brain/api/items?kind=rule"]);
	});

	test("a path parameter, encoded", async () => {
		const { fetch, calls } = fakeFetch([json(200, [])]);

		await createClient(environment(fetch)).get("/items/{id}/versions", {
			path: { id: "a/b c" },
		});

		expect(calls.map(({ url }) => url)).toEqual([
			"/brain/api/items/a%2Fb%20c/versions",
		]);
	});

	test("a path that needs a parameter refuses to go without it", () => {
		const { fetch } = fakeFetch([]);
		const client = createClient(environment(fetch));

		// @ts-expect-error The schema requires the id.
		expect(() => client.get("/items/{id}/versions")).toThrow(
			"/items/{id}/versions needs a value for {id}",
		);
	});
});
