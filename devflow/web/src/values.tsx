// A project's values step, in its Setup panel: each variable its setup run listed, with why, whether
// it has a value and when it was given, a field to give one, and the values the owner added. A value
// is sent once, sealed by Dev flow, and never shown again: the page holds only what the owner is
// typing, and clears it once sent. See "Setting up a project" in devflow/README.md.
import { Button, Status, TextField } from "@platform/ui";
import { type FormEvent, useCallback, useEffect, useState } from "react";
import { describe } from "./actions";
import type { GetAnswer } from "./api/client";
import styles from "./setup.module.css";

export type ProjectValueList = GetAnswer<"/projects/{id}/values">;
export type ProjectValueName = ProjectValueList["values"][number];

/** How the page reads and gives a project's values, Dev flow's API in the
 * page and a test's own in a test. */
export type ValuesApi = {
	loadValues: (id: string) => Promise<ProjectValueList>;
	/** Seals the value under the name; it is never answered again. */
	giveValue: (id: string, name: string, value: string) => Promise<void>;
	removeValue: (id: string, name: string) => Promise<void>;
};

export const VALUES_LINE =
	"Give each value this project's tests or its start need: a throwaway value, or a key made for the automation with a low limit, never production's. Every job that runs its code gets them, and Dev flow hides them in what it stores and posts. Once given, a value is never shown again.";
export const NOTHING_LISTED = "Its setup run listed no values.";
export const NOT_LOADED = "The values could not be read. Reload the page.";
export const ADDED = "You added it.";

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

/** The values step: the names, each given or missing, and a way to add
 * one the setup run did not list. */
export function ValuesSection({
	id,
	loadValues,
	giveValue,
	removeValue,
	onChanged,
}: ValuesApi & {
	id: string;
	/** Reads the setup again, so the step's done shows what changed. */
	onChanged: () => Promise<unknown>;
}) {
	const [list, setList] = useState<ProjectValueList | null>();

	const reload = useCallback(
		() =>
			loadValues(id).then(
				(answer) => setList(answer),
				() => setList(null),
			),
		[id, loadValues],
	);

	useEffect(() => {
		void reload();
	}, [reload]);

	const changed = async () => {
		await reload();
		await onChanged();
	};

	if (list === undefined) return null;
	return (
		<section className={styles.run} aria-label="The values">
			<p className={styles.label}>The values</p>
			<p className={styles.line}>{VALUES_LINE}</p>
			{list === null ? (
				<p className={styles.error} role="alert">
					{NOT_LOADED}
				</p>
			) : (
				<>
					{list.values.some((value) => value.asked) ? null : (
						<p className={styles.muted}>{NOTHING_LISTED}</p>
					)}
					{list.values.length === 0 ? null : (
						<ul className={styles.values} aria-label="Values">
							{list.values.map((value) => (
								<ValueRow
									key={value.name}
									id={id}
									value={value}
									giveValue={giveValue}
									removeValue={removeValue}
									changed={changed}
								/>
							))}
						</ul>
					)}
					<AddValue id={id} giveValue={giveValue} changed={changed} />
				</>
			)}
		</section>
	);
}

/** One name: why it is needed, whether it has a value, and the field to
 * give or replace it, or why it cannot be given. */
function ValueRow({
	id,
	value,
	giveValue,
	removeValue,
	changed,
}: {
	id: string;
	value: ProjectValueName;
	giveValue: ValuesApi["giveValue"];
	removeValue: ValuesApi["removeValue"];
	changed: () => Promise<void>;
}) {
	const given = value.set_at !== null;
	const [replacing, setReplacing] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const [removing, setRemoving] = useState(false);

	const remove = async () => {
		setRemoving(true);
		try {
			await removeValue(id, value.name);
			setError(null);
			await changed();
		} catch (failure) {
			setError(describe(failure));
		}
		setRemoving(false);
	};

	return (
		<li className={styles.value}>
			<p className={styles.line}>
				{value.problem !== null ? (
					<Status tone="failed">Cannot be given</Status>
				) : given ? (
					<Status tone="done">Given</Status>
				) : (
					<Status tone="needs-you">Missing</Status>
				)}
				<span className={styles.mono}>{value.name}</span>
			</p>
			<p className={styles.muted}>{value.why ?? ADDED}</p>
			{value.problem === null ? null : (
				<p className={styles.muted}>
					The name {value.problem}, so it cannot be given here.
				</p>
			)}
			{value.set_at === null ? null : (
				<p className={styles.muted}>
					Given{" "}
					<time className={styles.mono} dateTime={value.set_at}>
						{DATE_TIME.format(new Date(value.set_at))}
					</time>
					.
				</p>
			)}
			{error === null ? null : (
				<p className={styles.error} role="alert">
					{error}
				</p>
			)}
			{value.problem !== null ? null : !given || replacing ? (
				<GiveValue
					id={id}
					name={value.name}
					giveValue={giveValue}
					given={async () => {
						setReplacing(false);
						await changed();
					}}
					cancel={given ? () => setReplacing(false) : null}
				/>
			) : (
				<div className={styles.actions}>
					<Button kind="secondary" onClick={() => setReplacing(true)}>
						Replace
					</Button>
					<Button kind="ghost" disabled={removing} onClick={remove}>
						Remove
					</Button>
				</div>
			)}
		</li>
	);
}

/** The field a value is typed in, as a password is, so it is not shown on
 * the screen or kept by the browser; cleared once sent. */
function GiveValue({
	id,
	name,
	giveValue,
	given,
	cancel,
}: {
	id: string;
	name: string;
	giveValue: ValuesApi["giveValue"];
	given: () => Promise<void>;
	cancel: (() => void) | null;
}) {
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const [error, setError] = useState<string | null>(null);

	const send = async (event: FormEvent) => {
		event.preventDefault();
		if (text === "") return;
		setSending(true);
		try {
			await giveValue(id, name, text);
			setText("");
			setError(null);
			await given();
		} catch (failure) {
			setError(describe(failure));
		}
		setSending(false);
	};

	return (
		<form className={styles.give} onSubmit={send}>
			<TextField
				label={`The value of ${name}`}
				type="password"
				autoComplete="off"
				spellCheck={false}
				value={text}
				error={error ?? undefined}
				onChange={(event) => setText(event.target.value)}
			/>
			<div className={styles.actions}>
				<Button type="submit" disabled={sending || text === ""}>
					Give
				</Button>
				{cancel === null ? null : (
					<Button kind="ghost" onClick={cancel}>
						Cancel
					</Button>
				)}
			</div>
		</form>
	);
}

/** A value the setup run did not list: its name and the value. */
function AddValue({
	id,
	giveValue,
	changed,
}: {
	id: string;
	giveValue: ValuesApi["giveValue"];
	changed: () => Promise<void>;
}) {
	const [adding, setAdding] = useState(false);
	const [name, setName] = useState("");
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const [error, setError] = useState<string | null>(null);

	const send = async (event: FormEvent) => {
		event.preventDefault();
		if (name.trim() === "" || text === "") return;
		setSending(true);
		try {
			await giveValue(id, name.trim(), text);
			setName("");
			setText("");
			setError(null);
			setAdding(false);
			await changed();
		} catch (failure) {
			setError(describe(failure));
		}
		setSending(false);
	};

	if (!adding) {
		return (
			<div>
				<Button kind="secondary" onClick={() => setAdding(true)}>
					Add a value
				</Button>
			</div>
		);
	}

	return (
		<form className={styles.give} onSubmit={send} aria-label="Add a value">
			<TextField
				label="Its name"
				help="Capital letters, digits and underscores, as the project reads it from its environment."
				autoComplete="off"
				spellCheck={false}
				value={name}
				onChange={(event) => setName(event.target.value)}
			/>
			<TextField
				label="The value"
				type="password"
				autoComplete="off"
				spellCheck={false}
				value={text}
				onChange={(event) => setText(event.target.value)}
			/>
			{error === null ? null : (
				<p className={styles.error} role="alert">
					{error}
				</p>
			)}
			<div className={styles.actions}>
				<Button
					type="submit"
					disabled={sending || name.trim() === "" || text === ""}
				>
					Give
				</Button>
				<Button
					kind="ghost"
					onClick={() => {
						setAdding(false);
						setName("");
						setText("");
						setError(null);
					}}
				>
					Cancel
				</Button>
			</div>
		</form>
	);
}
