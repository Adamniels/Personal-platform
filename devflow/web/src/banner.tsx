// The strip that says GitHub is faked, on every Dev flow page, from GET /devflow/api/status. Dev
// flow fakes GitHub wherever it is not told otherwise, the laptop's tests, CI and the runner's
// stack, and must never do so where real data is, so the page says so, and cannot be told to stop
// saying it. It is this page's own, since no other feature needs it. See "The code host seam" in
// devflow/docs/devflow-architecture.md.
import { useEffect, useState } from "react";
import { createClient, type GetAnswer } from "./api/client";
import styles from "./banner.module.css";

export type Status = GetAnswer<"/status">;

const client = createClient();

export function loadStatus(): Promise<Status> {
	return client.get("/status");
}

export type CodeHostBannerProps = {
	/** How the status is read, the service's by default; a test passes its
	 * own. */
	loadStatus?: () => Promise<Status>;
};

/** "Fake GitHub. Nothing here is real." in the attention style, in the fake
 * mode, and nothing otherwise: before the status answers, with GitHub, or
 * when the status cannot be read, since the page cannot tell then. */
export function CodeHostBanner({
	loadStatus: load = loadStatus,
}: CodeHostBannerProps) {
	const [fake, setFake] = useState(false);

	useEffect(() => {
		let current = true;
		load().then(
			(status) => {
				if (current) setFake(status.code_host === "fake");
			},
			() => {
				if (current) setFake(false);
			},
		);
		return () => {
			current = false;
		};
	}, [load]);

	if (!fake) return null;
	return (
		<div className={styles.banner} role="note">
			Fake GitHub. Nothing here is real.
		</div>
	);
}
