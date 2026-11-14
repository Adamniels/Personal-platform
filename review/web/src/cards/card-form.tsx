// The card form, to add a card or edit one in place: Front, Back, Deck with "New deck…", and the
// topic picker. Each is required and refused empty here, before anything is sent. Adding, it stays
// open after a save with the deck and topics kept, Front and Back cleared and focus on Front, and it
// remembers the deck and topics for the next visit, so capturing costs almost nothing. Ctrl or Cmd
// with Enter saves. See "The page" in review/README.md.
import { Button, Select, TextArea, TextField, useToast } from "@platform/ui";
import {
	type FormEvent,
	type KeyboardEvent,
	useEffect,
	useId,
	useRef,
	useState,
} from "react";
import type { ApiError } from "../api/client";
import { useAddDeck, useDecks } from "../decks/decks";
import { isFieldError } from "../providers";
import { TopicPicker } from "../topics/topic-picker";
import {
	MOST_TOPICS,
	type PickedTopic,
	resolveSlug,
	useBrainTopics,
} from "../topics/topics";
import { type Card, useAddCard, useEditCard } from "./cards";
import styles from "./cards.module.css";
import { readRemembered, remember } from "./remembered";

/** The Deck select's value for "New deck…". */
const NEW_DECK = "new";

type Errors = {
	front?: string;
	back?: string;
	deck?: string;
	newDeck?: string;
	topics?: string;
};

export function CardForm({
	card,
	onClose,
}: {
	/** The card to edit; left out, the form adds one. */
	card?: Card;
	onClose: () => void;
}) {
	const editing = card !== undefined;
	const decks = useDecks();
	const brainTopics = useBrainTopics();
	const addDeck = useAddDeck();
	const addCard = useAddCard();
	const editCard = useEditCard();
	const toast = useToast();
	const frontField = useRef<HTMLTextAreaElement>(null);
	const unavailableId = useId();

	const [front, setFront] = useState(card?.front ?? "");
	const [back, setBack] = useState(card?.back ?? "");
	const [deck, setDeck] = useState(card?.deck.id ?? "");
	const [newDeck, setNewDeck] = useState("");
	const [topics, setTopics] = useState<PickedTopic[]>(
		card?.topics.map(({ slug, name }) => ({ slug, name })) ?? [],
	);
	const [errors, setErrors] = useState<Errors>({});
	const [saving, setSaving] = useState(false);

	useEffect(() => {
		frontField.current?.focus();
	}, []);

	// Adding, the last deck and topics are offered once what they name is
	// known, and dropped silently where the owner no longer has them.
	const [remembered] = useState(() => (editing ? null : readRemembered()));
	const offeredDeck = useRef(false);
	const offeredTopics = useRef(false);
	useEffect(() => {
		if (!remembered || offeredDeck.current || !decks.data) return;
		offeredDeck.current = true;
		const last = remembered.deck_id;
		if (last && decks.data.some(({ id }) => id === last)) {
			setDeck((chosen) => chosen || last);
		}
	}, [remembered, decks.data]);
	useEffect(() => {
		if (!remembered || offeredTopics.current || !brainTopics.data) return;
		offeredTopics.current = true;
		const known = brainTopics.data;
		const offered: PickedTopic[] = [];
		for (const { slug } of remembered.topics) {
			const topic = resolveSlug(slug, known);
			if (topic && !offered.some((each) => each.slug === topic.slug)) {
				offered.push(topic);
			}
		}
		setTopics((picked) =>
			picked.length > 0 ? picked : offered.slice(0, MOST_TOPICS),
		);
	}, [remembered, brainTopics.data]);

	const unavailable = brainTopics.isError;

	function check(): Errors {
		const found: Errors = {};
		if (front.trim() === "") found.front = "Write the front";
		if (back.trim() === "") found.back = "Write the back";
		if (deck === "") found.deck = "Choose a deck";
		if (deck === NEW_DECK && newDeck.trim() === "") {
			found.newDeck = "Name the new deck";
		}
		if (topics.length === 0) found.topics = "Pick at least one topic";
		return found;
	}

	async function save() {
		if (saving || unavailable) return;
		const found = check();
		setErrors(found);
		if (Object.keys(found).length > 0) return;
		setSaving(true);
		try {
			const deckId = await deckToSave();
			if (deckId === undefined) return;
			const fields = {
				deck_id: deckId,
				front: front.trim(),
				back: back.trim(),
				topics,
			};
			if (card) {
				await editCard.mutateAsync({
					id: card.id,
					version: card.version,
					...fields,
				});
				toast.success("Card saved");
				onClose();
				return;
			}
			await addCard.mutateAsync(fields);
			remember({ deck_id: deckId, topics });
			toast.success("Card added");
			setFront("");
			setBack("");
			frontField.current?.focus();
		} catch (failure) {
			if (isFieldError(failure)) showRefusal(failure);
		} finally {
			setSaving(false);
		}
	}

	/** The deck the card goes in, made first when "New deck…" is chosen, or
	 * undefined when making it was refused. */
	async function deckToSave(): Promise<string | undefined> {
		if (deck !== NEW_DECK) return deck;
		try {
			const made = await addDeck.mutateAsync(newDeck.trim());
			// Chosen from now on, so a refused card does not make it twice.
			setDeck(made.id);
			setNewDeck("");
			return made.id;
		} catch (failure) {
			if (isFieldError(failure)) {
				const { detail, errors: named, title } = failure.problem;
				setErrors({ newDeck: named.name?.[0] ?? detail ?? title });
			}
			return undefined;
		}
	}

	/** A 422 on the field it names, as review names them on the wire; a 409,
	 * a card changed since it was read, as a toast. */
	function showRefusal(failure: ApiError) {
		const { status, detail, errors: named, title } = failure.problem;
		if (status === 409) {
			toast.error(
				"This card changed since it was opened. Close the form and edit it again.",
			);
			return;
		}
		const shown: Errors = {};
		for (const [field, messages] of Object.entries(named)) {
			const message = messages[0];
			if (field === "front") shown.front ??= message;
			else if (field === "back") shown.back ??= message;
			else if (field === "deck_id") shown.deck ??= message;
			else if (field.startsWith("topics")) shown.topics ??= message;
		}
		if (Object.keys(shown).length === 0) {
			toast.error(detail ?? title);
			return;
		}
		setErrors(shown);
	}

	function submit(event: FormEvent) {
		event.preventDefault();
		void save();
	}

	function onKeyDown(event: KeyboardEvent<HTMLFormElement>) {
		if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
			event.preventDefault();
			void save();
		}
	}

	return (
		<form
			className={[styles.form, editing ? styles.inRow : ""]
				.filter(Boolean)
				.join(" ")}
			aria-label={editing ? "Edit card" : "New card"}
			noValidate
			onSubmit={submit}
			onKeyDown={onKeyDown}
		>
			<div className={styles.sides}>
				<TextArea
					ref={frontField}
					label="Front"
					rows={3}
					value={front}
					error={errors.front}
					onChange={(event) => setFront(event.target.value)}
				/>
				<TextArea
					label="Back"
					rows={3}
					value={back}
					error={errors.back}
					onChange={(event) => setBack(event.target.value)}
				/>
			</div>
			<div className={styles.about}>
				{deck === NEW_DECK ? (
					<div className={styles.newDeck}>
						<TextField
							label="New deck name"
							help="Made when the card is saved."
							value={newDeck}
							error={errors.newDeck}
							maxLength={100}
							onChange={(event) => setNewDeck(event.target.value)}
						/>
						<Button
							kind="ghost"
							onClick={() => {
								setDeck("");
								setErrors(({ newDeck: _, ...rest }) => rest);
							}}
						>
							Choose a deck
						</Button>
					</div>
				) : (
					<Select
						label="Deck"
						value={deck}
						error={errors.deck}
						onChange={(event) => setDeck(event.target.value)}
					>
						<option value="" disabled>
							Choose a deck
						</option>
						{(decks.data ?? []).map((each) => (
							<option key={each.id} value={each.id}>
								{each.name}
							</option>
						))}
						<option value={NEW_DECK}>New deck…</option>
					</Select>
				)}
				<TopicPicker
					value={topics}
					onChange={(picked) => {
						setTopics(picked);
						setErrors(({ topics: _, ...rest }) => rest);
					}}
					error={errors.topics}
					unavailableId={unavailableId}
				/>
			</div>
			<div className={styles.formActions}>
				<Button
					type="submit"
					kind="primary"
					disabled={saving || unavailable}
					aria-describedby={unavailable ? unavailableId : undefined}
				>
					Save
				</Button>
				<Button kind="ghost" onClick={onClose}>
					Close
				</Button>
			</div>
		</form>
	);
}
