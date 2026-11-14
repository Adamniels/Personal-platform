// The topic picker: a card's one to five topics, picked from the owner's topics in the brain or
// created there by name. It is the ARIA combobox pattern, a text field that controls a listbox
// (https://www.w3.org/WAI/ARIA/apg/patterns/combobox/), built from the package's TextField and Chip.
// It stays in review's frontend until a second feature needs it, as "Only a component more than
// one feature needs goes in" in design/ui/README.md says.
import { Button, Chip, TextField } from "@platform/ui";
import { type KeyboardEvent, useId, useState } from "react";
import { isFieldError } from "../providers";
import styles from "./topic-picker.module.css";
import {
	MOST_TOPICS,
	type PickedTopic,
	suggestionsFor,
	useBrainTopics,
	useCreateTopic,
} from "./topics";

export const TOPICS_UNAVAILABLE =
	"Topics are unavailable while the brain cannot be reached";

type Option =
	| { kind: "topic"; id: string; topic: PickedTopic }
	| { kind: "create"; id: string; name: string };

export function TopicPicker({
	value,
	onChange,
	error,
	unavailableId,
}: {
	/** The topics picked, in order. */
	value: readonly PickedTopic[];
	onChange: (topics: PickedTopic[]) => void;
	/** What the form says is wrong, such as no topic picked. */
	error?: string;
	/** The id of the line that says the brain cannot be reached, so the form
	 * can name it as the reason Save is disabled. */
	unavailableId: string;
}) {
	const topics = useBrainTopics();
	const create = useCreateTopic();
	const [text, setText] = useState("");
	const [open, setOpen] = useState(false);
	const [active, setActive] = useState(0);
	const [ownError, setOwnError] = useState<string | undefined>();
	const listId = useId();
	const optionPrefix = useId();

	const unavailable = topics.isError;
	const full = value.length >= MOST_TOPICS;
	const { matches, create: creatable } = suggestionsFor(
		text,
		topics.data ?? [],
		value,
	);
	const options: Option[] = [
		...matches.map((topic) => ({
			kind: "topic" as const,
			id: `${optionPrefix}-${topic.id}`,
			topic: { slug: topic.slug, name: topic.name },
		})),
		...(creatable === undefined
			? []
			: [
					{
						kind: "create" as const,
						id: `${optionPrefix}-create`,
						name: creatable,
					},
				]),
	];
	const expanded = open && !full && !unavailable && options.length > 0;
	const current = expanded
		? options[Math.min(active, options.length - 1)]
		: undefined;

	function add(topic: PickedTopic) {
		if (full || value.some(({ slug }) => slug === topic.slug)) return;
		onChange([...value, topic]);
		setText("");
		setActive(0);
		setOwnError(undefined);
	}

	async function choose(option: Option) {
		if (option.kind === "topic") {
			add(option.topic);
			return;
		}
		if (create.isPending) return;
		try {
			const made = await create.mutateAsync(option.name);
			add({ slug: made.slug, name: made.name });
		} catch (failure) {
			if (isFieldError(failure)) {
				setOwnError(failure.problem.detail ?? failure.problem.title);
			}
		}
	}

	function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
		switch (event.key) {
			case "ArrowDown":
			case "ArrowUp": {
				event.preventDefault();
				if (!expanded) {
					setOpen(true);
					return;
				}
				const step = event.key === "ArrowDown" ? 1 : -1;
				setActive((index) => (index + step + options.length) % options.length);
				return;
			}
			case "Enter":
				// Enter picks; it never submits the card from this field.
				if (event.ctrlKey || event.metaKey) return;
				event.preventDefault();
				if (current) void choose(current);
				return;
			case "Escape":
				if (!expanded) return;
				event.preventDefault();
				event.stopPropagation();
				setOpen(false);
				return;
			case "Backspace":
				if (text === "" && value.length > 0) {
					onChange(value.slice(0, -1));
				}
				return;
		}
	}

	return (
		<div className={styles.picker}>
			<div className={styles.combo}>
				<TextField
					label="Topics"
					help="What this card is about. Up to five."
					error={error ?? ownError}
					value={text}
					disabled={unavailable}
					role="combobox"
					autoComplete="off"
					aria-autocomplete="list"
					aria-expanded={expanded}
					aria-controls={listId}
					aria-activedescendant={current?.id}
					aria-describedby={unavailable ? unavailableId : undefined}
					onChange={(event) => {
						setText(event.target.value);
						setOpen(true);
						setActive(0);
						setOwnError(undefined);
					}}
					onKeyDown={onKeyDown}
					onBlur={() => setOpen(false)}
				/>
				{expanded ? (
					<div
						id={listId}
						role="listbox"
						aria-label="Topics"
						className={styles.list}
					>
						{options.map((option) => (
							// The field keeps focus and names the active option, so an
							// option is never in the tab order; the keyboard reaches it
							// through the field, as the pattern has it.
							// biome-ignore lint/a11y/useKeyWithClickEvents: the field handles the keys for its listbox.
							<div
								key={option.id}
								id={option.id}
								role="option"
								tabIndex={-1}
								aria-selected={option === current}
								className={styles.option}
								// Keeps focus in the field, so the click is not lost to a blur.
								onMouseDown={(event) => event.preventDefault()}
								onClick={() => void choose(option)}
							>
								{option.kind === "topic"
									? option.topic.name
									: `Create topic ${option.name}`}
							</div>
						))}
					</div>
				) : null}
			</div>
			{unavailable ? (
				<p id={unavailableId} className={styles.unavailable}>
					{TOPICS_UNAVAILABLE}
				</p>
			) : null}
			{value.length > 0 ? (
				<ul className={styles.picked} aria-label="Picked topics">
					{value.map((topic) => (
						<li key={topic.slug} className={styles.pick}>
							<Chip>{topic.name}</Chip>
							<Button
								kind="ghost"
								className={styles.remove}
								aria-label={`Remove ${topic.name}`}
								onClick={() =>
									onChange(value.filter(({ slug }) => slug !== topic.slug))
								}
							>
								<span aria-hidden="true">×</span>
							</Button>
						</li>
					))}
				</ul>
			) : null}
		</div>
	);
}
