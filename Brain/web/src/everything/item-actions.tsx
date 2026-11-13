// What can be done to an item from its page, one item at a time: correct it, retire it, bring a
// retired one back, and mark a decision as contradicting another and take that back. Correcting
// and retiring use Profile's form, dialog and toasts, with the same words. Nothing here deletes
// anything: a retired item stays, and purging is a terminal command, "Purging" in Brain/README.md.
import { Button, Dialog, Select, useToast } from "@platform/ui";
import {
	type FormEvent,
	type RefObject,
	useEffect,
	useRef,
	useState,
} from "react";
import { navigate } from "../address";
import {
	useEditEntry,
	useRestoreEntry,
	useRetireEntry,
} from "../entries/entries";
import { EntryForm } from "../entries/entry-form";
import section from "../entries/section.module.css";
import { itemPath } from "./everything-paths";
import page from "./item-page.module.css";
import {
	type ListedItem,
	useEndContradiction,
	useMarkContradicting,
} from "./queries";
import type { Kind } from "./words";

/** The kinds the brain takes a correction of, `POST /items/{id}/edit`. A
 * level or a focus is set again on its topic's page, and a record or a
 * decision is not rewritten. */
const CORRECTABLE: ReadonlySet<Kind> = new Set(["profile", "rule", "state"]);

const MARK = "Mark as contradicting…";

/** The page's buttons, and the form or dialog one of them opens: Edit and
 * Retire for an active item, Edit only where the brain corrects its kind;
 * Bring back for a retired one; Mark as contradicting… for an active
 * decision. A replaced item offers none, since only the newest version can
 * be corrected. */
export function ItemActions({
	item,
	everything,
	contradicting,
	search,
	heading,
}: {
	item: ListedItem;
	everything: readonly ListedItem[];
	/** The ids of the items it is marked as contradicting now. */
	contradicting: ReadonlySet<string>;
	search: string;
	/** The page's heading, where focus goes when the button pressed is gone. */
	heading: RefObject<HTMLHeadingElement | null>;
}) {
	const edit = useEditEntry();
	const retire = useRetireEntry();
	const restore = useRestoreEntry();
	const toast = useToast();
	const [open, setOpen] = useState<"edit" | "retire" | "mark" | null>(null);
	const [refocus, setRefocus] = useState(false);
	const active = item.status === "active";
	const retired = item.inactive_reason === "retired";

	useEffect(() => {
		if (!refocus) return;
		heading.current?.focus();
		setRefocus(false);
	}, [refocus, heading]);

	async function save(content: string) {
		// The same text again is no correction, so nothing is sent.
		if (content === item.content) {
			setOpen(null);
			return;
		}
		const corrected = await edit.mutateAsync({ entry: item, content });
		toast.success("Entry saved");
		// The correction is a new version, with a page of its own.
		navigate(`${itemPath(corrected.id)}${search}`, { replace: true });
	}

	async function retireItem() {
		await retire.mutateAsync(item.id);
		setOpen(null);
		setRefocus(true);
		toast.success("Entry retired", {
			label: "Undo",
			onAction: () => restore.mutate(item.id),
		});
	}

	async function bringBack() {
		await restore.mutateAsync(item.id);
		setRefocus(true);
		toast.success("Entry brought back");
	}

	const buttons = [];
	if (active && CORRECTABLE.has(item.kind)) {
		buttons.push(
			<Button key="edit" onClick={() => setOpen("edit")}>
				Edit
			</Button>,
		);
	}
	if (active && item.kind === "decision") {
		buttons.push(
			<Button key="mark" onClick={() => setOpen("mark")}>
				{MARK}
			</Button>,
		);
	}
	if (active) {
		buttons.push(
			<Button key="retire" onClick={() => setOpen("retire")}>
				Retire
			</Button>,
		);
	}
	if (retired) {
		buttons.push(
			<Button key="restore" disabled={restore.isPending} onClick={bringBack}>
				Bring back
			</Button>,
		);
	}
	if (buttons.length === 0) return null;

	return (
		<>
			{open === "edit" || open === "mark" ? null : (
				<div className={page.actions}>{buttons}</div>
			)}
			{open === "edit" ? (
				<div className={`${section.panel} ${section.form}`}>
					<EntryForm
						label="Entry"
						name="Correct the entry"
						initial={item.content}
						emptyError="Write the entry first"
						onSave={save}
						onCancel={() => setOpen(null)}
					/>
				</div>
			) : null}
			{open === "mark" ? (
				<div className={`${section.panel} ${section.form}`}>
					<ChooseContradicting
						item={item}
						choices={everything.filter(
							(other) =>
								other.kind === "decision" &&
								other.status === "active" &&
								other.id !== item.id &&
								!contradicting.has(other.id),
						)}
						onDone={() => setOpen(null)}
					/>
				</div>
			) : null}
			{open === "retire" ? (
				<Dialog
					open
					title="Retire this entry?"
					confirmLabel="Retire entry"
					kind="destructive"
					onConfirm={() =>
						retireItem()
							// A failure has raised its toast already.
							.catch(() => setOpen(null))
					}
					onCancel={() => setOpen(null)}
				>
					It stops being used. It stays in Everything, where you can bring it
					back.
				</Dialog>
			) : null}
		</>
	);
}

/** A select of the other active decisions, with Save and Cancel. */
function ChooseContradicting({
	item,
	choices,
	onDone,
}: {
	item: ListedItem;
	choices: readonly ListedItem[];
	onDone: () => void;
}) {
	const mark = useMarkContradicting();
	const toast = useToast();
	const [chosen, setChosen] = useState("");
	const [error, setError] = useState<string | undefined>();
	const select = useRef<HTMLSelectElement>(null);

	useEffect(() => {
		select.current?.focus();
	}, []);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (mark.isPending) return;
		if (chosen === "") {
			setError("Choose the decision it contradicts");
			return;
		}
		try {
			await mark.mutateAsync({ id: item.id, other: chosen });
		} catch {
			// The failure has raised its toast, and the form stays open.
			return;
		}
		toast.success("Marked as contradicting");
		onDone();
	}

	return (
		<form className={page.form} aria-label={MARK} noValidate onSubmit={submit}>
			<Select
				ref={select}
				label="Contradicting decision"
				help={
					choices.length === 0
						? "Add the other decision on History first."
						: "Both stay. An answer holding both shows them as a conflict."
				}
				value={chosen}
				error={error}
				onChange={(event) => {
					setChosen(event.target.value);
					setError(undefined);
				}}
			>
				<option value="" disabled>
					{choices.length === 0 ? "No other decision yet" : "Choose a decision"}
				</option>
				{choices.map((choice) => (
					<option key={choice.id} value={choice.id}>
						{choice.content}
					</option>
				))}
			</Select>
			<div className={page.actions}>
				<Button type="submit" disabled={mark.isPending}>
					Save
				</Button>
				<Button kind="ghost" onClick={onDone}>
					Cancel
				</Button>
			</div>
		</form>
	);
}

/** Takes a contradiction back, from the line in Related that shows it. */
export function TakeBack({
	relationId,
	describedBy,
}: {
	relationId: string;
	/** The id of the other item's link, which says what is taken back. */
	describedBy: string;
}) {
	const end = useEndContradiction();
	const toast = useToast();

	async function takeBack() {
		try {
			await end.mutateAsync(relationId);
		} catch {
			// The failure has raised its toast.
			return;
		}
		toast.success("Contradiction taken back");
	}

	return (
		<Button
			kind="ghost"
			aria-describedby={describedBy}
			disabled={end.isPending}
			onClick={takeBack}
		>
			Take back
		</Button>
	);
}
