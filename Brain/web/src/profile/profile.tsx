// The Profile section: who the owner is, one card per category, as "Authority follows the store,
// not the source" in Brain/docs/trust-model.md has the profile. Only the owner changes it, so
// everything here is entered by hand, and an entry takes no subjects here.
import { Button, Card, EmptyState, Select, useToast } from "@platform/ui";
import { useEffect, useRef, useState } from "react";
import { type Entry, useAddEntry, useEntries } from "../entries/entries";
import styles from "../entries/entries.module.css";
import { EntryForm } from "../entries/entry-form";
import { EntryList } from "../entries/entry-list";
import layout from "./profile.module.css";

/** The profile's categories, in the order of their cards. */
export const CATEGORIES = [
	{ key: "identity", label: "Identity" },
	{ key: "goals", label: "Goals" },
	{ key: "values", label: "Values" },
	{ key: "interests", label: "Interests" },
	{ key: "preferences", label: "Preferences" },
	{ key: "working_style", label: "Working style" },
] as const;

type Category = (typeof CATEGORIES)[number];

/** Which add form is open: the section's, a card's, or none. */
type Adding = "section" | Category["key"] | null;

export function Profile() {
	const entries = useEntries("profile");
	const add = useAddEntry();
	const toast = useToast();
	const [adding, setAdding] = useState<Adding>(null);

	async function save(category: string, content: string) {
		await add.mutateAsync({ kind: "profile", category, content });
		setAdding(null);
		toast.success("Entry saved");
	}

	if (entries.isPending) {
		return <p className={styles.none}>Loading the profile…</p>;
	}
	if (entries.isError) {
		return (
			<EmptyState
				title="The profile could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}

	return (
		<div className={layout.section}>
			<div className={layout.toolbar}>
				<Button kind="primary" onClick={() => setAdding("section")}>
					Add entry
				</Button>
			</div>
			{adding === "section" ? (
				<div className={layout.panel}>
					<AddEntryForm onSave={save} onCancel={() => setAdding(null)} />
				</div>
			) : null}
			<div className={layout.grid}>
				{CATEGORIES.map((category) => (
					<CategoryCard
						key={category.key}
						category={category}
						entries={entries.data.filter(
							(entry) => entry.category === category.key,
						)}
						adding={adding === category.key}
						onAdd={() => setAdding(category.key)}
						onSave={(content) => save(category.key, content)}
						onCancel={() => setAdding(null)}
					/>
				))}
			</div>
		</div>
	);
}

function CategoryCard({
	category,
	entries,
	adding,
	onAdd,
	onSave,
	onCancel,
}: {
	category: Category;
	entries: readonly Entry[];
	adding: boolean;
	onAdd: () => void;
	onSave: (content: string) => Promise<unknown>;
	onCancel: () => void;
}) {
	return (
		<Card
			title={category.label}
			footer={adding ? undefined : <Button onClick={onAdd}>Add</Button>}
		>
			<div className={layout.body}>
				{entries.length === 0 ? (
					<p className={styles.none}>
						No {category.label.toLowerCase()} entries yet.
					</p>
				) : (
					<EntryList entries={entries} />
				)}
				{adding ? (
					<EntryForm
						label="New entry"
						emptyError="Write the entry first"
						onSave={onSave}
						onCancel={onCancel}
					/>
				) : null}
			</div>
		</Card>
	);
}

/** The section's own add form, which asks for the category first. */
function AddEntryForm({
	onSave,
	onCancel,
}: {
	onSave: (category: string, content: string) => Promise<unknown>;
	onCancel: () => void;
}) {
	const [category, setCategory] = useState("");
	const [error, setError] = useState<string | undefined>();
	const select = useRef<HTMLSelectElement>(null);

	useEffect(() => {
		select.current?.focus();
	}, []);

	return (
		<EntryForm
			name="Add entry"
			label="New entry"
			emptyError="Write the entry first"
			focusText={false}
			validate={() => {
				setError(category === "" ? "Choose a category" : undefined);
				return category !== "";
			}}
			onSave={(content) => onSave(category, content)}
			onCancel={onCancel}
		>
			<Select
				ref={select}
				label="Category"
				value={category}
				error={error}
				onChange={(event) => {
					setCategory(event.target.value);
					setError(undefined);
				}}
			>
				<option value="" disabled>
					Choose a category
				</option>
				{CATEGORIES.map(({ key, label }) => (
					<option key={key} value={key}>
						{label}
					</option>
				))}
			</Select>
		</EntryForm>
	);
}
