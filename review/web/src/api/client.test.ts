import { describe, expect, test, vi } from "vitest";
import { ApiError, type ClientEnvironment, createClients } from "./client";

const createClient = (env: ClientEnvironment) => createClients(env).review;

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
			pathname: "/review/cards",
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
			"/review/api/whoami",
			"/auth/refresh",
			"/review/api/whoami",
		]);
		const refresh = calls[1]?.init;
		expect(refresh?.method).toBe("POST");
		expect(refresh?.redirect).toBe("manual");
		expect(refresh?.credentials).toBe("same-origin");
		expect(new Headers(refresh?.headers).get("content-type")).toBe(
			"application/x-www-form-urlencoded",
		);
		expect(String(refresh?.body)).toBe("return_to=%2Freview%2Fcards%3Fq%3Da+b");
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
				"/auth/refresh?return_to=%2Freview%2Fcards%3Fq%3Da%20b",
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
			"/cards",
			{ front: "What is a monad?" },
		);

		expect(answer).toEqual({ id: "x" });
		const call = calls[0];
		expect(call?.url).toBe("/review/api/cards");
		expect(call?.init.method).toBe("POST");
		expect(call?.init.credentials).toBe("same-origin");
		expect(new Headers(call?.init.headers).get("content-type")).toBe(
			"application/json",
		);
		expect(call?.init.body).toBe('{"front":"What is a monad?"}');
	});

	test("a 204 answers nothing", async () => {
		const { fetch } = fakeFetch([empty(204)]);

		const answer = await createClient(environment(fetch)).send(
			"DELETE",
			"/cards/x",
		);

		expect(answer).toBeUndefined();
	});

	async function problemOf(answer: Response | (() => never)) {
		const { fetch } = fakeFetch([answer]);
		const error = await createClient(environment(fetch))
			.send("POST", "/cards", {})
			.catch((caught: unknown) => caught);
		expect(error).toBeInstanceOf(ApiError);
		return (error as ApiError).problem;
	}

	test("a problem document is read as it is", async () => {
		const problem = await problemOf(
			json(
				409,
				{
					type: "https://tools.ietf.org/html/rfc9110#section-15.5.10",
					title: "Conflict",
					status: 409,
					detail: "The card is already in that deck.",
				},
				"application/problem+json",
			),
		);

		expect(problem).toEqual({
			status: 409,
			title: "Conflict",
			detail: "The card is already in that deck.",
			errors: {},
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
			errors: {},
		});
		expect(
			await problemOf(() => {
				throw new TypeError("Failed to fetch");
			}),
		).toEqual({
			status: 0,
			title: "Review could not be reached",
			detail: null,
			errors: {},
		});
	});
});

describe("a GET goes to its path under /review/api/", () => {
	test("health", async () => {
		const { fetch, calls } = fakeFetch([json(200, { status: "ok" })]);

		const answer = await createClient(environment(fetch)).get("/health");

		expect(answer).toEqual({ status: "ok" });
		expect(calls.map(({ url }) => url)).toEqual(["/review/api/health"]);
		expect(new Headers(calls[0]?.init.headers).get("accept")).toBe(
			"application/json",
		);
	});
});

describe("a 422 names its fields", () => {
	test("review's errors are read by the field they name", async () => {
		const { fetch } = fakeFetch([
			json(
				422,
				{
					title: "One or more validation errors occurred.",
					status: 422,
					detail: "The front is empty.",
					errors: { front: ["The front is empty."], "topics[1].slug": [1] },
				},
				"application/problem+json",
			),
		]);

		const error = await createClient(environment(fetch))
			.send("POST", "/cards", {})
			.catch((caught: unknown) => caught);

		expect((error as ApiError).problem.errors).toEqual({
			front: ["The front is empty."],
		});
	});
});

describe("a GET fills its address from its parameters", () => {
	test("a query value is added, and an undefined one left out", async () => {
		const { fetch, calls } = fakeFetch([json(200, []), json(200, [])]);
		const client = createClient(environment(fetch));

		await client.get("/cards", { query: { deck: "d 1" } });
		await client.get("/cards", { query: { deck: undefined } });

		expect(calls.map(({ url }) => url)).toEqual([
			"/review/api/cards?deck=d+1",
			"/review/api/cards",
		]);
	});
});

describe("the brain's client", () => {
	test("calls under /brain/api/, and says the brain could not be reached", async () => {
		const { fetch, calls } = fakeFetch([
			json(200, []),
			() => {
				throw new TypeError("Failed to fetch");
			},
		]);
		const { brain } = createClients(environment(fetch));

		await brain.get("/topics");
		const error = await brain.get("/topics").catch((caught: unknown) => caught);

		expect(calls.map(({ url }) => url)).toEqual([
			"/brain/api/topics",
			"/brain/api/topics",
		]);
		expect((error as ApiError).problem.title).toBe(
			"The brain could not be reached",
		);
	});

	test("shares one refresh with review's", async () => {
		const { fetch, calls } = fakeFetch([
			empty(401),
			empty(401),
			empty(200),
			json(200, { account_id: "a" }),
			json(200, []),
		]);
		const { review, brain } = createClients(environment(fetch));

		await Promise.all([review.get("/whoami"), brain.get("/topics")]);

		expect(calls.filter((call) => call.url === "/auth/refresh")).toHaveLength(
			1,
		);
	});
});
