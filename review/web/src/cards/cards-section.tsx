// The Cards tab: a Deck filter and "Add card", the tab's one primary button, which opens the card
// form in a Card above the list. The list is the package's Table, newest first: Front, Deck, Topics
// and Next due, each row with Edit, which opens the same form in the row, and Delete, which asks
// first. On phone the Table folds Deck, Topics and Next due into one line under Front, so each card
// is a stacked row. Only one form is open at a time, so the view has one primary button. See "The
// page" in review/README.md.
import {
	Button,
	Chip,
	Dialog,
	EmptyState,
	Card as Panel,
	Select,
	Table,
	type TableColumn,
	useToast,
} from "@platform/ui";
import { useEffect, useId, useState } from "react";
import { navigate } from "../address";
import { useDecks } from "../decks/decks";
import { useClock } from "../providers";
import { CardForm } from "./card-form";
import { type Card, useCards, useDeleteCard } from "./cards";
import styles from "./cards.module.css";
import { exactTime, firstLine, relativeDue } from "./when";

/** Which form is open: the add form, or the edit form of one card. */
type Open = { kind: "add" } | { kind: "edit"; id: string } | null;

export function CardsSection() {
	const decks = useDecks();
	const [filter, setFilter] = useState("");
	const cards = useCards(filter === "" ? undefined : filter);
	const [open, setOpen] = useState<Open>(null);
	const [deleting, setDeleting] = useState<Card | null>(null);
	// The card whose Edit takes focus back once its form closes.
	const [refocus, setRefocus] = useState<string | null>(null);
	const remove = useDeleteCard();
	const toast = useToast();
	const now = useClock();
	const ids = useId();
	const editId = (card: Card) => `${ids}-edit-${card.id}`;
	const frontId = (card: Card) => `${ids}-front-${card.id}`;

	useEffect(() => {
		if (refocus === null) return;
		document.getElementById(refocus)?.focus();
		setRefocus(null);
	}, [refocus]);

	if (decks.isPending) return <p className={styles.note}>Loading the decks…</p>;
	if (decks.isError) {
		return <p className={styles.note}>The decks could not be loaded.</p>;
	}
	if (decks.data.length === 0) {
		return (
			<EmptyState
				className={styles.empty}
				title="Make a deck first"
				detail="Every card lives in a deck. Make one, then add cards to it."
				action={
					<a
						className={styles.link}
						href="/review/decks"
						onClick={(event) => {
							event.preventDefault();
							navigate("/review/decks");
						}}
					>
						Go to Decks
					</a>
				}
			/>
		);
	}

	const editing = open?.kind === "edit" ? open.id : null;
	const noCardsAtAll =
		filter === "" && cards.data !== undefined && cards.data.length === 0;

	function closeEdit(card: Card) {
		setOpen(null);
		setRefocus(editId(card));
	}

	const columns: TableColumn<Card>[] = [
		{
			key: "front",
			header: "Front",
			cell: (card) =>
				card.id === editing ? (
					<CardForm card={card} onClose={() => closeEdit(card)} />
				) : (
					<span id={frontId(card)} className={styles.front}>
						{firstLine(card.front)}
					</span>
				),
		},
		{
			key: "deck",
			header: "Deck",
			priority: 2,
			cell: (card) => (card.id === editing ? null : card.deck.name),
			fold: (card) => (card.id === editing ? "" : card.deck.name),
		},
		{
			key: "topics",
			header: "Topics",
			priority: 2,
			cell: (card) =>
				card.id === editing ? null : (
					<span className={styles.chips}>
						{card.topics.map((topic) => (
							<Chip key={topic.slug}>{topic.name}</Chip>
						))}
					</span>
				),
			fold: (card) =>
				card.id === editing
					? ""
					: card.topics.map(({ name }) => name).join(", "),
		},
		{
			key: "due",
			header: "Next due",
			priority: 2,
			numeric: true,
			cell: (card) =>
				card.id === editing ? null : (
					<time dateTime={card.due} title={exactTime(card.due)}>
						{relativeDue(card.due, now())}
					</time>
				),
			fold: (card) =>
				card.id === editing ? "" : `due ${relativeDue(card.due, now())}`,
		},
	];

	return (
		<div className={styles.section}>
			<div className={styles.toolbar}>
				<div className={styles.filter}>
					<Select
						label="Deck"
						value={filter}
						onChange={(event) => setFilter(event.target.value)}
					>
						<option value="">All decks</option>
						{decks.data.map((deck) => (
							<option key={deck.id} value={deck.id}>
								{deck.name}
							</option>
						))}
					</Select>
				</div>
				{open === null && !noCardsAtAll ? (
					<Button kind="primary" onClick={() => setOpen({ kind: "add" })}>
						Add card
					</Button>
				) : null}
			</div>
			{open?.kind === "add" ? (
				<Panel title="New card">
					<CardForm onClose={() => setOpen(null)} />
				</Panel>
			) : null}
			{cards.isPending ? (
				<p className={styles.note}>Loading the cards…</p>
			) : cards.isError ? (
				<p className={styles.note}>The cards could not be loaded.</p>
			) : cards.data.length === 0 ? (
				noCardsAtAll ? (
					<EmptyState
						title="No cards yet"
						detail="A card is a question and its answer, about one to five topics."
						action={
							open === null ? (
								<Button onClick={() => setOpen({ kind: "add" })}>
									Add card
								</Button>
							) : undefined
						}
					/>
				) : (
					<EmptyState
						title="No cards in this deck"
						detail="Choose All decks to see every card."
					/>
				)
			) : (
				<Table
					label="Cards"
					columns={columns}
					rows={cards.data}
					rowKey={(card) => card.id}
					rowState={(card) => (card.id === editing ? "selected" : undefined)}
					rowAction={(card) =>
						card.id === editing ? null : (
							<span className={styles.rowActions}>
								<Button
									id={editId(card)}
									kind="ghost"
									aria-describedby={frontId(card)}
									onClick={() => setOpen({ kind: "edit", id: card.id })}
								>
									Edit
								</Button>
								<Button
									kind="ghost"
									aria-describedby={frontId(card)}
									onClick={() => setDeleting(card)}
								>
									Delete
								</Button>
							</span>
						)
					}
				/>
			)}
			{deleting ? (
				<Dialog
					open
					title="Delete this card?"
					confirmLabel="Delete card"
					kind="destructive"
					onConfirm={() =>
						remove
							.mutateAsync(deleting.id)
							.then(() => toast.success("Card deleted"))
							// A failure has raised its toast already.
							.catch(() => undefined)
							.finally(() => setDeleting(null))
					}
					onCancel={() => setDeleting(null)}
				>
					Its review history goes with it.
				</Dialog>
			) : null}
		</div>
	);
}
