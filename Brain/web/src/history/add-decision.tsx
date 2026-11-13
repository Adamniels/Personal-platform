// Adding a decision: what was decided, why, what it was made over, and when, today unless said,
// as "Decisions" in Brain/docs/event-model.md has one, a record that it was made and a claim
// that it holds.
import { Button, TextArea, TextField, useToast } from "@platform/ui";
import { useEffect, useRef, useState } from "react";
import { EntryForm } from "../entries/entry-form";
import { SubjectFields } from "../subjects/subject-fields";
import { NO_SUBJECTS, subjectsOf } from "../subjects/subjects";
import layout from "./history.module.css";
import { useAddDecision } from "./queries";
import { occurredAt, today, type WhenDraft } from "./when";
import { WhenFields } from "./when-fields";

/** As many rejected options as POST /items takes. */
const MOST_REJECTED = 20;

export function AddDecision({ onDone }: { onDone: () => void }) {
	const add = useAddDecision();
	const toast = useToast();
	const [reasoning, setReasoning] = useState("");
	const [reasoningError, setReasoningError] = useState<string | undefined>();
	const [rejected, setRejected] = useState<string[]>([""]);
	const [when, setWhen] = useState<WhenDraft>({
		precision: "day",
		day: today(),
	});
	const [whenError, setWhenError] = useState<string | undefined>();
	const [subjects, setSubjects] = useState(NO_SUBJECTS);

	async function save(content: string) {
		const at = occurredAt(when);
		if (at === undefined) return;
		await add.mutateAsync({
			content,
			details: {
				reasoning: reasoning.trim(),
				rejected: rejected
					.map((option) => option.trim())
					.filter((option) => option !== ""),
			},
			occurred_at: at,
			occurred_precision: when.precision,
			...subjectsOf(subjects),
		});
		onDone();
		toast.success("Decision saved");
	}

	return (
		<EntryForm
			name="Add decision"
			label="What was decided"
			emptyError="Write what was decided first"
			validate={() => {
				const reasoned = reasoning.trim() !== "";
				const dated = occurredAt(when) !== undefined;
				setReasoningError(reasoned ? undefined : "Say why it was decided");
				setWhenError(dated ? undefined : "Say when it was decided");
				return reasoned && dated;
			}}
			after={
				<>
					<TextArea
						label="Why"
						rows={3}
						value={reasoning}
						error={reasoningError}
						onChange={(event) => {
							setReasoning(event.target.value);
							setReasoningError(undefined);
						}}
					/>
					<Rejected value={rejected} onChange={setRejected} />
					<WhenFields
						legend="When it was decided"
						value={when}
						error={whenError}
						onChange={(next) => {
							setWhen(next);
							setWhenError(undefined);
						}}
					/>
					<SubjectFields value={subjects} onChange={setSubjects} />
				</>
			}
			onSave={save}
			onCancel={onDone}
		/>
	);
}

/** What was rejected, a line at a time: one field to start with, and Add
 * another for the next, which takes focus. An empty line is left out. */
function Rejected({
	value,
	onChange,
}: {
	value: string[];
	onChange: (value: string[]) => void;
}) {
	const fields = useRef<(HTMLInputElement | null)[]>([]);
	const [added, setAdded] = useState(false);

	useEffect(() => {
		if (!added) return;
		fields.current[value.length - 1]?.focus();
		setAdded(false);
	}, [added, value.length]);

	return (
		<fieldset className={layout.group}>
			<legend className={layout.legend}>What was rejected</legend>
			{value.map((option, index) => (
				<TextField
					// The lines only grow, so an index is a line's identity.
					// biome-ignore lint/suspicious/noArrayIndexKey: see above
					key={index}
					ref={(field) => {
						fields.current[index] = field;
					}}
					label={`Option ${index + 1}`}
					autoComplete="off"
					value={option}
					onChange={(event) =>
						onChange(
							value.map((each, at) =>
								at === index ? event.target.value : each,
							),
						)
					}
				/>
			))}
			{value.length < MOST_REJECTED ? (
				<div>
					<Button
						kind="ghost"
						onClick={() => {
							onChange([...value, ""]);
							setAdded(true);
						}}
					>
						Add another
					</Button>
				</div>
			) : null}
		</fieldset>
	);
}
