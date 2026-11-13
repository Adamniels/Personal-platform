// The Knowledge section: every topic the owner knows, with how well and what he is doing about it,
// as "How level is represented" in Brain/docs/knowledge-model.md has them. The table reads the
// knowledge view, `GET /knowledge`, one row per topic that is not an alias, so an alias never is a
// row: it answers under its target.
import {
	Badge,
	Button,
	EmptyState,
	FilterRow,
	Select,
	Table,
	type TableColumn,
	type TableSort,
	TextField,
	useToast,
} from "@platform/ui";
import { type FormEvent, useEffect, useRef, useState } from "react";
import { usePathname } from "../address";
import section from "../entries/section.module.css";
import { Link } from "../link";
import { isFieldError } from "../providers";
import { pageAt, sectionAt } from "../sections";
import { FocusSelect, LevelSelect } from "./fields";
import styles from "./knowledge.module.css";
import { topicPath } from "./knowledge-paths";
import {
	FOCUSES,
	type Focus,
	focusWord,
	LEVELS,
	type Level,
	lastSet,
	levelRank,
	levelWord,
	NOT_SET,
} from "./levels";
import {
	type KnowledgeRow,
	useAddTopic,
	useKnowledge,
	useSaveSettings,
} from "./queries";
import { TopicPage } from "./topic-page";

/** When a level or focus was last set, as a day: "13 Nov 2026". */
const DAY = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
	year: "numeric",
});

/** The section: a topic's page when the address names one, the table
 * otherwise. */
export function Knowledge() {
	const pathname = usePathname();
	const here = sectionAt(pathname);
	const slug = here && pageAt(here, pathname);
	return slug === undefined ? <Overview /> : <TopicPage slug={slug} />;
}

function Overview() {
	const knowledge = useKnowledge();
	const [adding, setAdding] = useState(false);

	if (knowledge.isPending) {
		return <p className={styles.note}>Loading what you know…</p>;
	}
	if (knowledge.isError) {
		return (
			<EmptyState
				title="Knowledge could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}

	const canonical = knowledge.data.filter((row) => row.state === "canonical");
	return (
		<div className={section.section}>
			<div className={section.toolbar}>
				<Button kind="primary" onClick={() => setAdding(true)}>
					Add topic
				</Button>
			</div>
			{adding ? (
				<div className={`${section.panel} ${section.form}`}>
					<AddTopicForm onDone={() => setAdding(false)} />
				</div>
			) : null}
			{canonical.length === 0 ? (
				<EmptyState
					title="No topics yet"
					detail="Add a topic to say how well you know it and what you are doing about it."
				/>
			) : (
				<TopicTable rows={canonical} />
			)}
		</div>
	);
}

type Filters = { search: string; level: Level | ""; focus: Focus | "" };

function matches(row: KnowledgeRow, filters: Filters): boolean {
	const search = filters.search.trim().toLocaleLowerCase();
	return (
		(search === "" || row.name.toLocaleLowerCase().includes(search)) &&
		(filters.level === "" || row.level?.value === filters.level) &&
		(filters.focus === "" || row.focus?.value === filters.focus)
	);
}

const byName = (a: KnowledgeRow, b: KnowledgeRow) =>
	a.name.localeCompare(b.name, "en", { sensitivity: "base" }) ||
	a.slug.localeCompare(b.slug);

/** Compares two values that may be missing, a missing one last whichever
 * way the column is sorted. */
function missingLast(
	a: number | undefined,
	b: number | undefined,
	sign: number,
): number {
	if (a === undefined || b === undefined) {
		return (a === undefined ? 1 : 0) - (b === undefined ? 1 : 0);
	}
	return (a - b) * sign;
}

function sorted(
	rows: readonly KnowledgeRow[],
	sort: TableSort,
): KnowledgeRow[] {
	const sign = sort.direction === "ascending" ? 1 : -1;
	const time = (row: KnowledgeRow) => {
		const at = lastSet(row);
		return at === undefined ? undefined : Date.parse(at);
	};
	const compare = (a: KnowledgeRow, b: KnowledgeRow): number => {
		if (sort.key === "level") {
			return (
				missingLast(
					levelRank(a.level?.value),
					levelRank(b.level?.value),
					sign,
				) || byName(a, b)
			);
		}
		if (sort.key === "last-set") {
			return missingLast(time(a), time(b), sign) || byName(a, b);
		}
		return byName(a, b) * sign;
	};
	return [...rows].sort(compare);
}

function count(n: number): string {
	return `${n} ${n === 1 ? "topic" : "topics"}`;
}

const COLUMNS: readonly TableColumn<KnowledgeRow>[] = [
	{
		key: "topic",
		header: "Topic",
		priority: 1,
		sortable: true,
		cell: (row) => (
			<Link className={styles.topic} href={topicPath(row.slug)}>
				{row.name}
			</Link>
		),
	},
	{
		key: "level",
		header: "Level",
		priority: 2,
		sortable: true,
		cell: (row) => levelWord(row.level?.value),
	},
	{
		key: "focus",
		header: "Focus",
		priority: 2,
		cell: (row) =>
			row.focus ? (
				<Badge>{focusWord(row.focus.value)}</Badge>
			) : (
				<span className={styles.unset}>{NOT_SET}</span>
			),
		fold: (row) => focusWord(row.focus?.value),
	},
	{
		key: "history",
		header: "History",
		priority: 3,
		numeric: true,
		cell: (row) => row.history_count,
	},
	{
		key: "last-set",
		header: "Last set",
		priority: 3,
		numeric: true,
		sortable: true,
		cell: (row) => {
			const at = lastSet(row);
			return at === undefined ? (
				"Never"
			) : (
				<time dateTime={at}>{DAY.format(new Date(at))}</time>
			);
		},
	},
];

/** A row's level and focus while it is edited, "" where it has none. */
type Draft = { level: string; focus: string };

function draftOf(row: KnowledgeRow): Draft {
	return { level: row.level?.value ?? "", focus: row.focus?.value ?? "" };
}

function TopicTable({ rows }: { rows: readonly KnowledgeRow[] }) {
	// The row being edited, by id, and what its fields hold.
	const [editing, setEditing] = useState<{ id: string; draft: Draft } | null>(
		null,
	);
	const saveSettings = useSaveSettings();
	const saving = useRef(false);
	const toast = useToast();
	const [sort, setSort] = useState<TableSort>({
		key: "topic",
		direction: "ascending",
	});
	const [filters, setFilters] = useState<Filters>({
		search: "",
		level: "",
		focus: "",
	});
	const shown = sorted(
		rows.filter((row) => matches(row, filters)),
		sort,
	);

	function change(field: keyof Draft, value: string) {
		setEditing(
			(current) =>
				current && { ...current, draft: { ...current.draft, [field]: value } },
		);
	}

	// Each column's fields while its row is edited, Level and Focus in place
	// of their cells.
	const columns = COLUMNS.map((column): TableColumn<KnowledgeRow> => {
		if (column.key === "level") {
			return {
				...column,
				edit: (row) => (
					<LevelSelect
						value={editing?.draft.level ?? ""}
						onChange={(value) => change("level", value)}
						unset={row.level === null}
					/>
				),
			};
		}
		if (column.key === "focus") {
			return {
				...column,
				edit: (row) => (
					<FocusSelect
						value={editing?.draft.focus ?? ""}
						onChange={(value) => change("focus", value)}
						unset={row.focus === null}
					/>
				),
			};
		}
		return column;
	});

	async function save(row: KnowledgeRow) {
		if (!editing || saving.current) return;
		saving.current = true;
		try {
			const changed = await saveSettings(row, editing.draft);
			if (changed) toast.success("Topic saved");
			setEditing(null);
		} catch {
			// The failure has raised its toast, and the row stays open.
		} finally {
			saving.current = false;
		}
	}

	return (
		<>
			<FilterRow
				role="search"
				aria-label="Filter topics"
				search={
					<TextField
						label="Search topics"
						type="search"
						value={filters.search}
						onChange={(event) =>
							setFilters({ ...filters, search: event.target.value })
						}
					/>
				}
				count={count(shown.length)}
			>
				<Select
					label="Level"
					value={filters.level}
					onChange={(event) =>
						setFilters({ ...filters, level: event.target.value as Level | "" })
					}
				>
					<option value="">Any</option>
					{LEVELS.map((level) => (
						<option key={level.value} value={level.value}>
							{level.word}
						</option>
					))}
				</Select>
				<Select
					label="Focus"
					value={filters.focus}
					onChange={(event) =>
						setFilters({ ...filters, focus: event.target.value as Focus | "" })
					}
				>
					<option value="">Any</option>
					{FOCUSES.map((focus) => (
						<option key={focus.value} value={focus.value}>
							{focus.word}
						</option>
					))}
				</Select>
			</FilterRow>
			{shown.length === 0 ? (
				<EmptyState
					title="No topics match"
					detail="Change the search or the filters to see more."
				/>
			) : (
				<Table
					label="Topics"
					columns={columns}
					rows={shown}
					rowKey={(row) => row.id}
					sort={sort}
					onSort={setSort}
					rowState={(row) => (row.id === editing?.id ? "editing" : undefined)}
					rowAction={(row) => (
						<Button
							kind="ghost"
							// Hidden on phone, where Level and Focus are not
							// columns to edit in: the topic's page edits them.
							className={styles.rowEdit}
							aria-label={`Edit ${row.name}`}
							onClick={() => setEditing({ id: row.id, draft: draftOf(row) })}
						>
							Edit
						</Button>
					)}
					onSave={save}
					onCancel={() => setEditing(null)}
				/>
			)}
		</>
	);
}

/** Asks for a topic's name and, if he likes, its level and focus. */
function AddTopicForm({ onDone }: { onDone: () => void }) {
	const add = useAddTopic();
	const toast = useToast();
	const [name, setName] = useState("");
	const [level, setLevel] = useState("");
	const [focus, setFocus] = useState("");
	const [error, setError] = useState<string | undefined>();
	const [saving, setSaving] = useState(false);
	const field = useRef<HTMLInputElement>(null);

	useEffect(() => {
		field.current?.focus();
	}, []);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (saving) return;
		const trimmed = name.trim();
		if (trimmed === "") {
			setError("Write the topic's name");
			return;
		}
		setSaving(true);
		try {
			await add.mutateAsync({
				name: trimmed,
				level: (level || undefined) as Level | undefined,
				focus: (focus || undefined) as Focus | undefined,
			});
			toast.success("Topic added");
			onDone();
		} catch (failure) {
			if (isFieldError(failure)) {
				setError(failure.problem.detail ?? failure.problem.title);
			}
		} finally {
			setSaving(false);
		}
	}

	return (
		<form
			className={styles.form}
			aria-label="Add topic"
			noValidate
			onSubmit={submit}
		>
			<TextField
				ref={field}
				label="Name"
				value={name}
				error={error}
				onChange={(event) => {
					setName(event.target.value);
					setError(undefined);
				}}
			/>
			<div className={styles.pair}>
				<LevelSelect value={level} onChange={setLevel} unset />
				<FocusSelect value={focus} onChange={setFocus} unset />
			</div>
			<div className={styles.actions}>
				<Button type="submit" disabled={saving}>
					Save
				</Button>
				<Button kind="ghost" onClick={onDone}>
					Cancel
				</Button>
			</div>
		</form>
	);
}
