// The GitHub link on the Projects tab: the one place the owner links the GitHub account Dev flow
// acts as, sees whom it is linked as, links it again when GitHub stopped taking it, and unlinks
// it. Starting a link asks Dev flow for the code host's address and sends the browser there; the
// code host sends it back to /devflow/github/callback, which comes back here. See "The page" in
// devflow/README.md and "Acting as the owner" in devflow/docs/devflow-architecture.md.
import { Button, Card, Dialog } from "@platform/ui";
import { type ReactElement, useState } from "react";
import styles from "./projects-link.module.css";
import type { Status } from "./status";

export const NOT_LINKED = "GitHub was not linked. Try again.";
export const NOT_UNLINKED = "GitHub was not unlinked. Try again.";

export type GitHubLinkProps = {
	/** The caller's link, undefined before the status answers. */
	github: Status["github"] | undefined;
	/** Whether the page was sent back from a return that did not link. */
	failed: boolean;
	/** Starts a link, answering the code host's address. */
	start: () => Promise<string>;
	/** Unlinks, settling once the page shows it. */
	unlink: () => Promise<void>;
	/** Sends the browser to an address. */
	go: (url: string) => void;
};

/** The link as a card of the package's: an invitation and "Link GitHub" when
 * nothing is linked, the login and a quiet "Unlink" behind a confirmation when
 * it is, and the attention style with "Link again" when GitHub stopped taking
 * it. Nothing before the status answers. */
export function GitHubLink({
	github,
	failed,
	start,
	unlink,
	go,
}: GitHubLinkProps) {
	const [error, setError] = useState<string | null>(failed ? NOT_LINKED : null);
	const [starting, setStarting] = useState(false);
	const [confirming, setConfirming] = useState(false);

	if (github === undefined) return null;

	const link = async () => {
		setStarting(true);
		try {
			go(await start());
		} catch {
			setError(NOT_LINKED);
			setStarting(false);
		}
	};

	const confirmUnlink = async () => {
		try {
			await unlink();
			setError(null);
		} catch {
			setError(NOT_UNLINKED);
		}
		setConfirming(false);
	};

	const linkButton = (label: string) => (
		<Button kind="primary" disabled={starting} onClick={link}>
			{label}
		</Button>
	);

	let line: string;
	let action: ReactElement;
	if (github.state === "linked") {
		line = `Linked as @${github.login}`;
		action = (
			<Button kind="ghost" onClick={() => setConfirming(true)}>
				Unlink
			</Button>
		);
	} else if (github.state === "needs_relink") {
		line = "Your GitHub link has expired.";
		action = linkButton("Link again");
	} else {
		line =
			"Link your GitHub account to connect repositories and to merge and comment as yourself.";
		action = linkButton("Link GitHub");
	}

	return (
		<>
			<Card
				title="GitHub"
				attention={github.state === "needs_relink"}
				className={styles.card}
				footer={action}
			>
				{error === null ? null : (
					<p className={styles.error} role="alert">
						{error}
					</p>
				)}
				<p className={styles.line}>{line}</p>
			</Card>
			<Dialog
				open={confirming}
				title="Unlink GitHub?"
				confirmLabel="Unlink GitHub"
				kind="destructive"
				onConfirm={confirmUnlink}
				onCancel={() => setConfirming(false)}
			>
				Dev flow forgets your GitHub account and its tokens. GitHub keeps its
				authorization of the app until you revoke it there.
			</Dialog>
		</>
	);
}
