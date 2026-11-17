// The Claude login on the Projects tab: the one place the owner gives Dev flow the token from
// `claude setup-token`, which agents run on his subscription with. The token goes in once and is
// never shown again, not even in part: the page knows only whether one is set, when it was given
// and when it is due, and shows the warning style from a month before. See "The pages" and
// "Claude login" in devflow/README.md.
import { Button, Card, Dialog, TextField } from "@platform/ui";
import { type FormEvent, useEffect, useState } from "react";
import { ApiError, type GetAnswer } from "./api/client";
import styles from "./claude-login.module.css";

export type ClaudeLoginStatus = GetAnswer<"/claude-login">;

export const NOT_A_TOKEN =
	"That is not a setup token. Make one with claude setup-token and paste all of it.";
export const NOT_SAVED = "The Claude login was not saved. Try again.";
export const NOT_REMOVED = "The Claude login was not removed. Try again.";

/** How the section reads and changes the login, Dev flow's API in the page
 * and a test's own in a test. */
export type ClaudeLoginApi = {
	loadLogin: () => Promise<ClaudeLoginStatus>;
	saveLogin: (token: string) => Promise<ClaudeLoginStatus>;
	removeLogin: () => Promise<void>;
};

/** The login as a card of the package's: what it is for, where it stands,
 * and a password field to give or replace it, with "Remove" behind a
 * confirmation once one is set. Nothing before the status answers. */
export function ClaudeLogin({
	loadLogin,
	saveLogin,
	removeLogin,
}: ClaudeLoginApi) {
	const [status, setStatus] = useState<ClaudeLoginStatus>();
	const [token, setToken] = useState("");
	const [saving, setSaving] = useState(false);
	const [invalid, setInvalid] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const [confirming, setConfirming] = useState(false);

	useEffect(() => {
		let current = true;
		loadLogin().then(
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
	}, [loadLogin]);

	if (status === undefined) return null;

	const save = async (event: FormEvent) => {
		event.preventDefault();
		setSaving(true);
		try {
			setStatus(await saveLogin(token));
			setInvalid(false);
			setError(null);
		} catch (refused) {
			const notAToken =
				refused instanceof ApiError && refused.problem.status === 422;
			setInvalid(notAToken);
			setError(notAToken ? null : NOT_SAVED);
		}
		// Emptied either way: the field never keeps a token, given or refused.
		setToken("");
		setSaving(false);
	};

	const confirmRemove = async () => {
		try {
			await removeLogin();
			setStatus({ set: false, given_at: null, renew_by: null, state: "none" });
			setError(null);
		} catch {
			setError(NOT_REMOVED);
		}
		setConfirming(false);
	};

	const due = status.state === "due_soon" || status.state === "expired";

	return (
		<>
			<Card title="Claude login" attention={due} className={styles.card}>
				<p className={styles.line}>
					Agents run on your subscription with this. Make it with{" "}
					<code className={styles.command}>claude setup-token</code>.
				</p>
				<Standing status={status} />
				{error === null ? null : (
					<p className={styles.error} role="alert">
						{error}
					</p>
				)}
				<form className={styles.form} onSubmit={save}>
					<TextField
						label="Claude token"
						type="password"
						autoComplete="off"
						spellCheck={false}
						value={token}
						error={invalid ? NOT_A_TOKEN : undefined}
						onChange={(event) => setToken(event.target.value)}
					/>
					<div className={styles.actions}>
						<Button
							kind="secondary"
							type="submit"
							disabled={saving || token.trim() === ""}
						>
							{status.set ? "Replace" : "Save"}
						</Button>
						{status.set ? (
							<Button
								kind="ghost"
								type="button"
								onClick={() => setConfirming(true)}
							>
								Remove
							</Button>
						) : null}
					</div>
				</form>
			</Card>
			<Dialog
				open={confirming}
				title="Remove the Claude login?"
				confirmLabel="Remove the login"
				kind="destructive"
				onConfirm={confirmRemove}
				onCancel={() => setConfirming(false)}
			>
				Dev flow forgets the token. Agents cannot run on your subscription until
				you give it a new one.
			</Dialog>
		</>
	);
}

/** Where the login stands, in words, its dates in the reader's own zone:
 * not set, set and when it is due, or, from a month before, the warning. */
function Standing({ status }: { status: ClaudeLoginStatus }) {
	const { given_at: given, renew_by: renewBy } = status;
	if (!status.set || given === null || renewBy === null) {
		return <p className={styles.line}>Not set yet.</p>;
	}
	if (status.state === "expired") {
		return (
			<p className={`${styles.line} ${styles.warning}`}>
				Set on <Day at={given} />. It ran out on <Day at={renewBy} />: agents
				cannot run until you replace it.
			</p>
		);
	}
	if (status.state === "due_soon") {
		return (
			<p className={`${styles.line} ${styles.warning}`}>
				Set on <Day at={given} />. Renew it by <Day at={renewBy} />: agents
				cannot run once it runs out.
			</p>
		);
	}
	return (
		<p className={styles.line}>
			Set on <Day at={given} />, renew by <Day at={renewBy} />.
		</p>
	);
}

const DATE = new Intl.DateTimeFormat("en-GB", { dateStyle: "medium" });

/** A day, in the reader's own zone. */
function Day({ at }: { at: string }) {
	return <time dateTime={at}>{DATE.format(new Date(at))}</time>;
}
