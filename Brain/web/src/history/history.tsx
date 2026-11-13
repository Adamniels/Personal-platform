// The History section: what happened and what was decided, in the order it happened, as "History
// and continuity" in Brain/docs/knowledge-model.md has it. A record is as true years later as the
// day it was entered, so nothing here asks again; when it happened is kept apart from when it was
// entered, and the timeline is ordered by the first.
import {
	Badge,
	Button,
	EmptyState,
	FilterRow,
	Select,
	Status,
	Timeline,
	TimelineEntry,
	useToast,
} from "@platform/ui";
import { type ReactNode, useState } from "react";
import { usePathname } from "../address";
import styles from "../entries/entries.module.css";
import { EntryForm } from "../entries/entry-form";
import section from "../entries/section.module.css";
import { Link } from "../link";
import { pageAt, sectionAt } from "../sections";
import { SubjectBadges, SubjectFields } from "../subjects/subject-fields";
import {
	NO_SUBJECTS,
	subjectsOf,
	useCanonicalTopics,
	useEntities,
} from "../subjects/subjects";
import { AddDecision } from "./add-decision";
import { DecisionPage } from "./decision-page";
import { decisionOf } from "./decisions";
import layout from "./history.module.css";
import { decisionPath } from "./history-paths";
import {
	ANY,
	type HistoryFilters,
	type HistoryRow,
	useAddHappened,
	useHistory,
} from "./queries";
import { occurredAt, type WhenDraft, writtenWhen } from "./when";
import { WhenFields } from "./when-fields";

export function History() {
	const pathname = usePathname();
	const here = sectionAt(pathname);
	const id = here && pageAt(here, pathname);
	return id === undefined ? <Overview /> : <DecisionPage id={id} />;
}

function Overview() {
	const [filters, setFilters] = useState<HistoryFilters>(ANY);
	const history = useHistory(filters);
	const [adding, setAdding] = useState<"happened" | "decision" | null>(null);

	const narrowed = filters.topic !== "" || filters.entity !== "";

	return (
		<div className={section.section}>
			<div className={section.toolbar}>
				<Filters value={filters} onChange={setFilters} />
				<div className={layout.buttons}>
					<Button onClick={() => setAdding("happened")}>
						Add what happened
					</Button>
					<Button onClick={() => setAdding("decision")}>Add decision</Button>
				</div>
			</div>
			{adding === "happened" ? (
				<div className={`${section.panel} ${section.form}`}>
					<AddHappened onDone={() => setAdding(null)} />
				</div>
			) : null}
			{adding === "decision" ? (
				<div className={`${section.panel} ${section.form}`}>
					<AddDecision onDone={() => setAdding(null)} />
				</div>
			) : null}
			{history.isPending ? (
				<p className={styles.none}>Loading the history…</p>
			) : history.isError ? (
				<EmptyState
					title="The history could not be loaded"
					detail="Reload the page to try again."
				/>
			) : history.data.length === 0 ? (
				narrowed ? (
					<EmptyState
						title="Nothing matches these filters"
						detail="Set a filter back to Any to see more."
					/>
				) : (
					<EmptyState
						title="Nothing in your history yet"
						detail="Add what happened, or a decision, and it takes its place by when it happened."
					/>
				)
			) : (
				<Timeline aria-label="History" className={section.panel}>
					{history.data.map((row) => (
						<HistoryEntry key={row.id} row={row} />
					))}
				</Timeline>
			)}
		</div>
	);
}

function HistoryEntry({ row }: { row: HistoryRow }) {
	const decision = decisionOf(row);
	const subjects =
		row.topics.length > 0 || row.entities.length > 0 ? (
			<SubjectBadges topics={row.topics} entities={row.entities} />
		) : null;
	const detail: ReactNode =
		decision || subjects ? (
			<span className={layout.detail}>
				{decision ? (
					<span className={layout.reasoning}>{decision.reasoning}</span>
				) : null}
				{subjects}
			</span>
		) : undefined;

	return (
		<TimelineEntry
			time={
				row.occurred_at
					? writtenWhen(row.occurred_at, row.occurred_precision ?? "instant")
					: ""
			}
			detail={detail}
		>
			{decision ? (
				<>
					<Badge className={layout.mark}>Decision</Badge>
					<Link className={layout.link} href={decisionPath(row.id)}>
						{row.content}
					</Link>
					{row.replaced_by ? (
						<Status className={layout.replaced} tone="idle">
							Replaced
						</Status>
					) : null}
				</>
			) : (
				<span className={layout.text}>{row.content}</span>
			)}
		</TimelineEntry>
	);
}

/** The filter row: a Topic select of the canonical topics and a Named
 * thing select of the user's named things, each with Any. */
function Filters({
	value,
	onChange,
}: {
	value: HistoryFilters;
	onChange: (value: HistoryFilters) => void;
}) {
	const topics = useCanonicalTopics();
	const entities = useEntities();
	return (
		<FilterRow
			role="search"
			aria-label="Filter history"
			className={layout.filters}
		>
			<Select
				label="Topic"
				value={value.topic}
				onChange={(event) => onChange({ ...value, topic: event.target.value })}
			>
				<option value="">Any</option>
				{(topics.data ?? []).map((topic) => (
					<option key={topic.id} value={topic.slug}>
						{topic.name}
					</option>
				))}
			</Select>
			<Select
				label="Named thing"
				value={value.entity}
				onChange={(event) => onChange({ ...value, entity: event.target.value })}
			>
				<option value="">Any</option>
				{(entities.data ?? []).map((entity) => (
					<option key={entity.id} value={entity.name}>
						{entity.name}
					</option>
				))}
			</Select>
		</FilterRow>
	);
}

const WHEN_ERROR = "Say when it happened";

/** Something that happened: its text, when, to the precision it is known
 * at, and its subjects. A day is asked for first, and left empty, since
 * only the owner knows it. */
function AddHappened({ onDone }: { onDone: () => void }) {
	const add = useAddHappened();
	const toast = useToast();
	const [when, setWhen] = useState<WhenDraft>({ precision: "day" });
	const [whenError, setWhenError] = useState<string | undefined>();
	const [subjects, setSubjects] = useState(NO_SUBJECTS);

	async function save(content: string) {
		const at = occurredAt(when);
		if (at === undefined) return;
		await add.mutateAsync({
			content,
			occurred_at: at,
			occurred_precision: when.precision,
			...subjectsOf(subjects),
		});
		onDone();
		toast.success("Entry saved");
	}

	return (
		<EntryForm
			name="Add what happened"
			label="What happened"
			emptyError="Write what happened first"
			validate={() => {
				const right = occurredAt(when) !== undefined;
				setWhenError(right ? undefined : WHEN_ERROR);
				return right;
			}}
			after={
				<>
					<WhenFields
						legend="When it happened"
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
