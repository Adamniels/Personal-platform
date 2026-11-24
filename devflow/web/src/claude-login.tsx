// The Claude login on the Projects tab: the one place the owner gives Dev flow the token from
// `claude setup-token`, which agents run on his subscription with. The token goes in once and is
// never shown again, not even in part: the page knows only whether one is set, when it was given
// and when it is due, and shows the warning style from a month before. See "The pages" and
// "Claude login" in devflow/README.md.
import { Button, Dialog, Status, TextField } from "@platform/ui";
import { type FormEvent, type ReactElement, useEffect, useState } from "react";
import { ApiError, type GetAnswer } from "./api/client";
import styles from "./claude-login.module.css";
import { Connection } from "./connection";
import line from "./connection.module.css";
import { KeyIcon } from "./icons";

export type ClaudeLoginStatus = GetAnswer<"/claude-login">;

export const NOT_A_TOKEN =
	"That is not a setup token. Make one with claude setup-token and paste all of it.";
export const NOT_SAVED = "The Claude login was not saved. Try again.";
export const NOT_REMOVED = "The Claude login was not removed. Try again.";
export const PURPOSE =
	"Agents run on your subscription with this. Make it with claude setup-token.";

/** How the section reads and changes the login, Dev flow's API in the page
 * and a test's own in a test. */
export type ClaudeLoginApi = {
	loadLogin: () => Promise<ClaudeLoginStatus>;
	saveLogin: (token: string) => Promise<ClaudeLoginStatus>;
	removeLogin: () => Promise<void>;
};

/** The login as one line of Connections: where it stands as a status label,
 * its dates on the mono line, and "Give a token" or "Replace", which opens a
 * password field below the line; "Remove", behind a confirmation, is beside
 * the field once one is set. From a month before it is due, the item takes
 * the attention style. Nothing before the status answers. */
export function ClaudeLogin({
	loadLogin,
	saveLogin,
	removeLogin,
}: ClaudeLoginApi) {
	const [status, setStatus] = useState<ClaudeLoginStatus>();
	const [open, setOpen] = useState(false);
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
			setOpen(false);
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

	const close = () => {
		setOpen(false);
		setToken("");
		setInvalid(false);
	};

	const confirmRemove = async () => {
		try {
			await removeLogin();
			setStatus({ set: false, given_at: null, renew_by: null, state: "none" });
			setError(null);
			setOpen(false);
		} catch {
			setError(NOT_REMOVED);
		}
		setConfirming(false);
	};

	const due = status.state === "due_soon" || status.state === "expired";

	return (
		<>
			<Connection
				name="Claude login"
				icon={<KeyIcon />}
				line={dates(status)}
				status={standing(status)}
				action={
					<Button
						kind="ghost"
						aria-expanded={open}
						onClick={() => setOpen(true)}
					>
						{status.set ? "Replace" : "Give a token"}
					</Button>
				}
				attention={due}
			>
				{error === null ? null : (
					<p className={line.error} role="alert">
						{error}
					</p>
				)}
				{open ? (
					<form className={styles.form} onSubmit={save}>
						<TextField
							label="Claude token"
							type="password"
							autoComplete="off"
							spellCheck={false}
							value={token}
							help={PURPOSE}
							error={invalid ? NOT_A_TOKEN : undefined}
							onChange={(event) => setToken(event.target.value)}
						/>
						<div className={styles.actions}>
							<Button
								kind="secondary"
								type="submit"
								disabled={saving || token.trim() === ""}
							>
								Save
							</Button>
							<Button kind="ghost" onClick={close}>
								Cancel
							</Button>
							{status.set ? (
								<Button
									kind="ghost"
									className={styles.remove}
									onClick={() => setConfirming(true)}
								>
									Remove
								</Button>
							) : null}
						</div>
					</form>
				) : null}
			</Connection>
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

/** Where the login stands, as a status label: not set, given, due within a
 * month, or past it. */
function standing(status: ClaudeLoginStatus): ReactElement {
	if (!status.set) return <Status tone="idle">Not set</Status>;
	if (status.state === "expired")
		return <Status tone="needs-you">Expired</Status>;
	if (status.state === "due_soon") return <Status tone="needs-you">Due</Status>;
	return <Status tone="done">Given</Status>;
}

/** The mono line: how to make one when none is set, otherwise when it was
 * given and when it is due or ran out, its days in the reader's own zone. */
function dates(status: ClaudeLoginStatus): string {
	const { given_at: given, renew_by: renewBy } = status;
	if (!status.set || given === null || renewBy === null) {
		return "make one with claude setup-token";
	}
	const end = status.state === "expired" ? "ran out" : "due";
	return `given ${day(given)} · ${end} ${day(renewBy)}`;
}

const DATE = new Intl.DateTimeFormat("en-GB", { dateStyle: "medium" });

/** A day, in the reader's own zone. */
const day = (at: string) => DATE.format(new Date(at));
