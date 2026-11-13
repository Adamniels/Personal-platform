// The Ask section: one of the brain's four questions, about now or about history, and exactly what
// a feature asking the same thing would get, so retrieval can be checked by using it before any
// feature exists to do the asking, as "The brain owns its own surface" in
// Brain/docs/brain-architecture.md has it. Nothing is sent until Ask is pressed, and changing what
// is asked clears the answer, so an answer on the page is always the answer to the form above it.
import {
	Button,
	EmptyState,
	FilterPill,
	Select,
	Tab,
	TabPanel,
	Tabs,
} from "@platform/ui";
import { type FormEvent, useState } from "react";
import section from "../entries/section.module.css";
import { type Subject, subjectFrom, subjectValue } from "../everything/filters";
import { useCanonicalTopics, useEntities } from "../subjects/subjects";
import { AnswerView } from "./answer";
import styles from "./ask.module.css";
import { useAsk } from "./queries";
import {
	bodyOf,
	type Draft,
	EMPTY_DRAFT,
	MAX_TOPICS,
	MODES,
	type Mode,
	QUESTIONS,
	type QuestionKey,
} from "./questions";

export const ASK_LINE =
	"Ask what a feature would ask, and see exactly what it gets.";

export function Ask() {
	const [question, setQuestion] = useState<QuestionKey>("profile");
	const [mode, setMode] = useState<Mode>("now");
	const [draft, setDraft] = useState<Draft>(EMPTY_DRAFT);
	const [missing, setMissing] = useState<string | null>(null);
	const asking = useAsk();

	/** Anything changing what is asked is a new question: the answer to the
	 * old one goes, and so does what the form said was missing. */
	function changed() {
		asking.reset();
		setMissing(null);
	}

	function submit(event: FormEvent<HTMLFormElement>) {
		event.preventDefault();
		const made = bodyOf(question, mode, draft);
		if ("missing" in made) {
			setMissing(made.missing);
			return;
		}
		setMissing(null);
		asking.mutate({ question, body: made.body });
	}

	return (
		<div className={section.section}>
			<p className={section.lede}>{ASK_LINE}</p>
			<Tabs
				label="Questions"
				value={question}
				onChange={(key) => {
					const next = QUESTIONS.find((each) => each.key === key);
					if (!next) return;
					setQuestion(next.key);
					changed();
				}}
			>
				{QUESTIONS.map((each) => (
					<Tab key={each.key} value={each.key}>
						{each.label}
					</Tab>
				))}
				{QUESTIONS.map((each) => (
					<TabPanel key={each.key} value={each.key} className={styles.panel}>
						{each.key === question ? (
							<>
								<form
									aria-label="Question"
									className={styles.form}
									noValidate
									onSubmit={submit}
								>
									<div className={styles.fields}>
										<Select
											label="About"
											value={mode}
											onChange={(event) => {
												setMode(event.target.value as Mode);
												changed();
											}}
										>
											{MODES.map(({ value, word }) => (
												<option key={value} value={value}>
													{word}
												</option>
											))}
										</Select>
										{question === "topics" ? (
											<TopicsField
												topics={draft.topics}
												error={missing}
												onChange={(topics) => {
													setDraft({ ...draft, topics });
													changed();
												}}
											/>
										) : null}
										{question === "history" ? (
											<SubjectField
												subject={draft.subject}
												error={missing}
												onChange={(subject) => {
													setDraft({ ...draft, subject });
													changed();
												}}
											/>
										) : null}
										<Button
											kind="primary"
											type="submit"
											className={styles.ask}
											disabled={asking.isPending}
										>
											Ask
										</Button>
									</div>
								</form>
								{asking.isPending ? (
									<p className={styles.note}>Asking the brain…</p>
								) : asking.data ? (
									<AnswerView asked={asking.data} />
								) : asking.isError ? (
									<EmptyState
										title="The question could not be answered"
										detail="Press Ask to try again."
									/>
								) : (
									<EmptyState
										title="Nothing asked yet"
										detail="Choose a question and press Ask."
									/>
								)}
							</>
						) : null}
					</TabPanel>
				))}
			</Tabs>
		</div>
	);
}

/** The topics for What do they know?: chosen one at a time from the
 * canonical ones, in the order chosen, each a pill that removes it. */
function TopicsField({
	topics,
	error,
	onChange,
}: {
	topics: readonly string[];
	error: string | null;
	onChange: (topics: string[]) => void;
}) {
	const canonical = useCanonicalTopics();
	const known = canonical.data ?? [];
	const full = topics.length >= MAX_TOPICS;
	const nameOf = (slug: string) =>
		known.find((topic) => topic.slug === slug)?.name ?? slug;

	return (
		<div className={styles.topics}>
			<Select
				label="Topic"
				value=""
				disabled={full}
				error={error ?? undefined}
				help={
					full
						? "Fifty topics, the most one question asks about."
						: "Asked in the order chosen, up to fifty."
				}
				onChange={(event) => {
					const slug = event.target.value;
					if (slug !== "" && !topics.includes(slug)) {
						onChange([...topics, slug]);
					}
				}}
			>
				<option value="">Choose a topic</option>
				{known
					.filter((topic) => !topics.includes(topic.slug))
					.map((topic) => (
						<option key={topic.id} value={topic.slug}>
							{topic.name}
						</option>
					))}
			</Select>
			{topics.length > 0 ? (
				<ul className={styles.pills} aria-label="Topics asked">
					{topics.map((slug) => (
						<li key={slug}>
							<FilterPill
								label={nameOf(slug)}
								onRemove={() => onChange(topics.filter((t) => t !== slug))}
							/>
						</li>
					))}
				</ul>
			) : null}
		</div>
	);
}

/** The one subject for What is the history?: a canonical topic or a named
 * thing, written as Everything's Subject filter writes it. */
function SubjectField({
	subject,
	error,
	onChange,
}: {
	subject: Subject | null;
	error: string | null;
	onChange: (subject: Subject | null) => void;
}) {
	const topics = useCanonicalTopics();
	const entities = useEntities();
	return (
		<Select
			label="Subject"
			value={subjectValue(subject)}
			error={error ?? undefined}
			onChange={(event) => onChange(subjectFrom(event.target.value))}
		>
			<option value="">Choose one</option>
			{topics.data?.length ? (
				<optgroup label="Topics">
					{topics.data.map((topic) => (
						<option key={topic.id} value={`topic:${topic.slug}`}>
							{topic.name}
						</option>
					))}
				</optgroup>
			) : null}
			{entities.data?.length ? (
				<optgroup label="Named things">
					{entities.data.map((entity) => (
						<option key={entity.id} value={`entity:${entity.name}`}>
							{entity.name}
						</option>
					))}
				</optgroup>
			) : null}
		</Select>
	);
}
