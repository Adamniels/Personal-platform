// The GitHub link on the Projects tab: the one place the owner links the GitHub account Dev flow
// acts as, sees whom it is linked as, links it again when GitHub stopped taking it, and unlinks
// it. Starting a link asks Dev flow for the code host's address and sends the browser there; the
// code host sends it back to /devflow/github/callback, which comes back here. See "The pages" in
// devflow/README.md and "Acting as the owner" in devflow/docs/devflow-architecture.md.
import { Button, Dialog, Status } from "@platform/ui";
import { type ReactElement, useState } from "react";
import { Connection } from "./connection";
import styles from "./connection.module.css";
import { BranchIcon } from "./icons";
import type { Status as PageStatus } from "./status";

export const NOT_LINKED = "GitHub was not linked. Try again.";
export const NOT_UNLINKED = "GitHub was not unlinked. Try again.";

export type GitHubLinkProps = {
	/** The caller's link, undefined before the status answers. */
	github: PageStatus["github"] | undefined;
	/** Whether the page was sent back from a return that did not link. */
	failed: boolean;
	/** Starts a link, answering the code host's address. */
	start: () => Promise<string>;
	/** Unlinks, settling once the page shows it. */
	unlink: () => Promise<void>;
	/** Sends the browser to an address. */
	go: (url: string) => void;
};

/** The link as one line of Connections: linked as the login, with a quiet
 * "Unlink" behind a confirmation; not linked, with "Link GitHub"; or expired,
 * in the attention style, with "Link again". Both are secondary, so "Connect a
 * repository" stays the view's one primary. An error is a line under the item.
 * Nothing before the status answers. */
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
		<Button kind="secondary" disabled={starting} onClick={link}>
			{label}
		</Button>
	);

	let line: string;
	let status: ReactElement;
	let action: ReactElement;
	if (github.state === "linked") {
		line = `linked as @${github.login}`;
		status = <Status tone="done">Linked</Status>;
		action = (
			<Button kind="ghost" onClick={() => setConfirming(true)}>
				Unlink
			</Button>
		);
	} else if (github.state === "needs_relink") {
		line = "your link has expired";
		status = <Status tone="needs-you">Expired</Status>;
		action = linkButton("Link again");
	} else {
		line = "to connect repositories and act as you";
		status = <Status tone="idle">Not linked</Status>;
		action = linkButton("Link GitHub");
	}

	return (
		<>
			<Connection
				name="GitHub"
				icon={<BranchIcon />}
				line={line}
				status={status}
				action={action}
				attention={github.state === "needs_relink"}
			>
				{error === null ? null : (
					<p className={styles.error} role="alert">
						{error}
					</p>
				)}
			</Connection>
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
