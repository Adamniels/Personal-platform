// The strip that says GitHub is faked, on every Dev flow page, from GET /devflow/api/status. Dev
// flow fakes GitHub wherever it is not told otherwise, the laptop's tests, CI and the runner's
// stack, and must never do so where real data is, so the page says so, and cannot be told to stop
// saying it. It is this page's own, since no other feature needs it. See "The code host seam" in
// devflow/docs/devflow-architecture.md.
import styles from "./banner.module.css";
import type { Status } from "./status";

export type CodeHostBannerProps = {
	/** The status, undefined before it answers or when it cannot be read. */
	status: Status | undefined;
};

/** "Fake GitHub. Nothing here is real." in the attention style, in the fake
 * mode, and nothing otherwise: before the status answers, with GitHub, or
 * when the status cannot be read, since the page cannot tell then. */
export function CodeHostBanner({ status }: CodeHostBannerProps) {
	if (status?.code_host !== "fake") return null;
	return (
		<div className={styles.banner} role="note">
			Fake GitHub. Nothing here is real.
		</div>
	);
}
