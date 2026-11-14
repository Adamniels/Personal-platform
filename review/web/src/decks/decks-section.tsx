// The Decks tab: the owner's decks by name, each with its card count, "Add deck" as the tab's one
// primary button, and Rename and Delete on each. A deck holding cards cannot be deleted, so its
// Delete is disabled and says why. See "The page" in review/README.md.
import {
	Badge,
	Button,
	type ButtonKind,
	Dialog,
	EmptyState,
	TextField,
	useToast,
} from "@platform/ui";
import { type FormEvent, useEffect, useId, useRef, useState } from "react";
import { isFieldError } from "../providers";
import {
	cardCount,
	type Deck,
	useAddDeck,
	useDecks,
	useDeleteDeck,
	useRenameDeck,
} from "./decks";
import styles from "./decks.module.css";

export function DecksSection() {
	const decks = useDecks();
	const add = useAddDeck();
	const [adding, setAdding] = useState(false);
	const addButton = useRef<HTMLButtonElement>(null);
	// Set when the add form closes, so focus returns to Add deck.
	const [refocus, setRefocus] = useState(false);

	useEffect(() => {
		if (!refocus || adding) return;
		addButton.current?.focus();
		setRefocus(false);
	}, [refocus, adding]);

	function closeAdd() {
		setAdding(false);
		setRefocus(true);
	}

	return (
		<div className={styles.section}>
			{adding ? (
				<DeckNameForm
					name="New deck"
					saveKind="primary"
					onSave={async (name) => {
						await add.mutateAsync(name);
						closeAdd();
					}}
					onCancel={closeAdd}
				/>
			) : (
				<div className={styles.toolbar}>
					<Button
						ref={addButton}
						kind="primary"
						onClick={() => setAdding(true)}
					>
						Add deck
					</Button>
				</div>
			)}
			{decks.isPending ? (
				<p className={styles.note}>Loading the decks…</p>
			) : decks.isError ? (
				<p className={styles.note}>The decks could not be loaded.</p>
			) : decks.data.length === 0 ? (
				<EmptyState
					title="No decks yet"
					detail="A deck holds the cards you study together. Add one to start."
				/>
			) : (
				<ul className={styles.list} aria-label="Decks">
					{decks.data.map((deck) => (
						<DeckRow key={deck.id} deck={deck} />
					))}
				</ul>
			)}
		</div>
	);
}

function DeckRow({ deck }: { deck: Deck }) {
	const [renaming, setRenaming] = useState(false);
	const [confirming, setConfirming] = useState(false);
	const [refocus, setRefocus] = useState(false);
	const renameButton = useRef<HTMLButtonElement>(null);
	const rename = useRenameDeck();
	const remove = useDeleteDeck();
	const toast = useToast();
	const nameId = useId();
	const whyId = useId();
	const holdsCards = deck.card_count > 0;

	useEffect(() => {
		if (!refocus || renaming) return;
		renameButton.current?.focus();
		setRefocus(false);
	}, [refocus, renaming]);

	function stopRenaming() {
		setRenaming(false);
		setRefocus(true);
	}

	if (renaming) {
		return (
			<li className={styles.row}>
				<DeckNameForm
					name={`Rename ${deck.name}`}
					initial={deck.name}
					saveKind="secondary"
					onSave={async (name) => {
						// The same name again is no change, so nothing is sent.
						if (name !== deck.name) {
							await rename.mutateAsync({ id: deck.id, name });
						}
						stopRenaming();
					}}
					onCancel={stopRenaming}
				/>
			</li>
		);
	}

	return (
		<li className={styles.row}>
			<span id={nameId} className={styles.name}>
				{deck.name}
			</span>
			<Badge>{cardCount(deck.card_count)}</Badge>
			<div className={styles.actions}>
				<Button
					ref={renameButton}
					kind="ghost"
					aria-describedby={nameId}
					onClick={() => setRenaming(true)}
				>
					Rename
				</Button>
				<Button
					kind="ghost"
					disabled={holdsCards}
					aria-describedby={holdsCards ? whyId : nameId}
					title={holdsCards ? "Move or delete its cards first." : undefined}
					onClick={() => setConfirming(true)}
				>
					Delete
				</Button>
				{holdsCards ? (
					<span id={whyId} className={styles.hidden}>
						Move or delete its cards first.
					</span>
				) : null}
			</div>
			{confirming ? (
				<Dialog
					open
					title="Delete this deck?"
					confirmLabel="Delete deck"
					kind="destructive"
					onConfirm={() =>
						remove
							.mutateAsync(deck.id)
							.then(() => toast.success("Deck deleted"))
							// A failure has raised its toast already.
							.catch(() => undefined)
							.finally(() => setConfirming(false))
					}
					onCancel={() => setConfirming(false)}
				>
					{deck.name} has no cards. Deleting it cannot be undone.
				</Dialog>
			) : null}
		</li>
	);
}

/** A deck's name with Save and Cancel, to add a deck or rename one. */
function DeckNameForm({
	name,
	initial = "",
	saveKind,
	onSave,
	onCancel,
}: {
	/** The form's accessible name. */
	name: string;
	initial?: string;
	/** Primary where the form stands in for Add deck, secondary in a row. */
	saveKind: ButtonKind;
	/** Saves the name, trimmed and never empty. A 409 or a 422 it throws is
	 * shown on the field; any other failure has its toast already. */
	onSave: (name: string) => Promise<unknown>;
	onCancel: () => void;
}) {
	const [text, setText] = useState(initial);
	const [error, setError] = useState<string | undefined>();
	const [saving, setSaving] = useState(false);
	const field = useRef<HTMLInputElement>(null);

	useEffect(() => {
		field.current?.focus();
	}, []);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (saving) return;
		const trimmed = text.trim();
		if (trimmed === "") {
			setError("Name the deck");
			return;
		}
		setError(undefined);
		setSaving(true);
		try {
			await onSave(trimmed);
		} catch (failure) {
			if (isFieldError(failure)) {
				const { detail, errors, title } = failure.problem;
				setError(errors.name?.[0] ?? detail ?? title);
			}
		} finally {
			setSaving(false);
		}
	}

	return (
		<form
			className={styles.form}
			aria-label={name}
			noValidate
			onSubmit={submit}
			onKeyDown={(event) => {
				if (event.key !== "Escape") return;
				event.preventDefault();
				onCancel();
			}}
		>
			<div className={styles.field}>
				<TextField
					ref={field}
					label="Name"
					value={text}
					error={error}
					maxLength={100}
					onChange={(event) => setText(event.target.value)}
				/>
			</div>
			<div className={styles.formActions}>
				<Button type="submit" kind={saveKind} disabled={saving}>
					Save
				</Button>
				<Button kind="ghost" onClick={onCancel}>
					Cancel
				</Button>
			</div>
		</form>
	);
}
