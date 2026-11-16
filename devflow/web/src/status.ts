// GET /devflow/api/status, read once as the page loads and again after the page changes what it
// says: which code host Dev flow talks to, for the banner, and the caller's GitHub link, for the
// Projects tab. See "The pages" in devflow/README.md.
import { useCallback, useEffect, useState } from "react";
import { createClient, type GetAnswer } from "./api/client";

export type Status = GetAnswer<"/status">;

const client = createClient();

export function loadStatus(): Promise<Status> {
	return client.get("/status");
}

/** The status, undefined before it answers or when it cannot be read, and a
 * way to read it again. */
export function useStatus(
	load: () => Promise<Status>,
): [Status | undefined, () => Promise<void>] {
	const [status, setStatus] = useState<Status>();

	const reload = useCallback(
		() =>
			load().then(
				(answer) => setStatus(answer),
				() => setStatus(undefined),
			),
		[load],
	);

	useEffect(() => {
		let current = true;
		load().then(
			(answer) => {
				if (current) setStatus(answer);
			},
			() => {
				if (current) setStatus(undefined);
			},
		);
		return () => {
			current = false;
		};
	}, [load]);

	return [status, reload];
}
