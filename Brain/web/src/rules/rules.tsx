// The Rules section: the owner's standing rules, global by construction. A rule exists only
// because the owner typed it here, so it applies at once, as "Standing rules are created
// deliberately, conversation instructions are temporary" in Brain/docs/trust-model.md has it.
import { Button, EmptyState, useToast } from "@platform/ui";
import { useState } from "react";
import { useAddEntry, useEntries } from "../entries/entries";
import styles from "../entries/entries.module.css";
import { EntryForm } from "../entries/entry-form";
import { EntryList } from "../entries/entry-list";
import section from "../entries/section.module.css";

export function Rules() {
	const rules = useEntries("rule");
	const add = useAddEntry();
	const toast = useToast();
	const [adding, setAdding] = useState(false);

	async function save(content: string) {
		await add.mutateAsync({ kind: "rule", content });
		setAdding(false);
		toast.success("Entry saved");
	}

	if (rules.isPending) {
		return <p className={styles.none}>Loading the rules…</p>;
	}
	if (rules.isError) {
		return (
			<EmptyState
				title="The rules could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}

	return (
		<div className={section.section}>
			<div className={section.toolbar}>
				<p className={section.lede}>
					How the platform and its agents should work with you. A rule applies
					everywhere, at once.
				</p>
				<Button kind="primary" onClick={() => setAdding(true)}>
					Add rule
				</Button>
			</div>
			{adding ? (
				<div className={`${section.panel} ${section.form}`}>
					<EntryForm
						name="Add rule"
						label="New rule"
						emptyError="Write the rule first"
						onSave={save}
						onCancel={() => setAdding(false)}
					/>
				</div>
			) : null}
			<EntryList
				entries={rules.data}
				className={section.panel}
				labels={{ field: "Rule", emptyError: "Write the rule first" }}
				empty={
					<EmptyState
						title="No rules yet"
						detail="A rule you add here applies everywhere, at once."
					/>
				}
			/>
		</div>
	);
}
