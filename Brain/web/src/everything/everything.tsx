// The Everything section: every item the brain holds, active or not, the general item browser of
// "The brain owns its own surface" in Brain/docs/brain-architecture.md. The table reads every
// item, GET /items, with when each was last supported and whether it may no longer be true, and
// narrows and pages it here, the filters kept in the address. Nothing here acts on an item: its
// page does, one at a time, and nothing anywhere deletes one.
import {
	Badge,
	EmptyState,
	FilterPill,
	FilterRow,
	Paging,
	Select,
	Status,
	Table,
	type TableColumn,
	type TableSort,
} from "@platform/ui";
import { navigate, usePathname, useSearch } from "../address";
import section from "../entries/section.module.css";
import { Link } from "../link";
import { pageAt, sectionAt } from "../sections";
import { SubjectBadges } from "../subjects/subject-fields";
import { useCanonicalTopics, useEntities } from "../subjects/subjects";
import styles from "./everything.module.css";
import { EVERYTHING_PATH, itemPath } from "./everything-paths";
import {
	DEFAULTS,
	type Filters,
	filtersFrom,
	STATE_FILTERS,
	type StateFilter,
	type Subject,
	searchOf,
	subjectFrom,
	subjectValue,
} from "./filters";
import { type Row, useEverything } from "./queries";
import { stateOf } from "./state";
import { KINDS, type Kind, kindWord } from "./words";

/** How many rows a page holds. */
export const PAGE_SIZE = 50;

/** When an item was last supported, as a day: "13 Nov 2026". */
const DAY = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
	year: "numeric",
});

export function dayOf(iso: string): string {
	return DAY.format(new Date(iso));
}

/** The section: an item's page when the address names one, the table
 * otherwise. */
export function Everything() {
	const pathname = usePathname();
	const here = sectionAt(pathname);
	const id = here && pageAt(here, pathname);
	return id === undefined ? <Overview /> : null;
}

/** Whether a row is among those the state filter shows. */
function inState(row: Row, filter: StateFilter): boolean {
	const { key } = stateOf(row);
	if (filter === "all") return true;
	if (filter === "current") return key === "active" || key === "stale";
	return key === filter;
}

function about(row: Row, subject: Subject | null): boolean {
	if (subject === null) return true;
	if (subject.kind === "topic") {
		return row.topics.some(({ slug }) => slug === subject.slug);
	}
	const name = subject.name.toLocaleLowerCase();
	return row.entities.some(
		(entity) => entity.name.toLocaleLowerCase() === name,
	);
}

/** Newest support first, or oldest when asked, the id breaking a tie the
 * same way each time. */
function sorted(rows: readonly Row[], sort: TableSort): Row[] {
	const sign = sort.direction === "ascending" ? 1 : -1;
	return [...rows].sort(
		(a, b) =>
			(Date.parse(a.last_supported_at) - Date.parse(b.last_supported_at)) *
				sign || a.id.localeCompare(b.id) * sign,
	);
}

/** The sort the address names: by last supported, newest first unless
 * `sort=oldest`. */
function sortFrom(search: string): TableSort {
	return {
		key: "last-supported",
		direction:
			new URLSearchParams(search).get("sort") === "oldest"
				? "ascending"
				: "descending",
	};
}

function count(n: number): string {
	return `${n} ${n === 1 ? "entry" : "entries"}`;
}

function Overview() {
	const search = useSearch();
	const filters = filtersFrom(search);
	const sort = sortFrom(search);
	const everything = useEverything();

	/** Goes to the list as these filters and this sort show it, back on its
	 * first page unless a page is asked for. */
	function show(
		next: Partial<Filters>,
		{ replace = false, by = sort }: { replace?: boolean; by?: TableSort } = {},
	) {
		const query = new URLSearchParams(
			searchOf({ ...filters, page: 1, ...next }).slice(1),
		);
		if (by.direction === "ascending") query.set("sort", "oldest");
		const written = query.toString();
		navigate(`${EVERYTHING_PATH}${written === "" ? "" : `?${written}`}`, {
			replace,
		});
	}

	if (everything.isPending) {
		return <p className={styles.note}>Loading everything…</p>;
	}
	if (everything.isError) {
		return (
			<EmptyState
				title="Everything could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}

	const shown = sorted(
		everything.data.filter(
			(row) =>
				(filters.kind === "" || row.kind === filters.kind) &&
				inState(row, filters.state) &&
				about(row, filters.subject),
		),
		sort,
	);
	const pages = Math.max(1, Math.ceil(shown.length / PAGE_SIZE));
	const page = Math.min(filters.page, pages);
	const from = (page - 1) * PAGE_SIZE;
	const rows = shown.slice(from, from + PAGE_SIZE);

	return (
		<div className={section.section}>
			<FilterBar filters={filters} total={shown.length} onChange={show} />
			{rows.length === 0 ? (
				<EmptyState
					title={
						everything.data.length === 0
							? "Nothing in your memory yet"
							: "Nothing matches"
					}
					detail={
						everything.data.length === 0
							? "What you add in the other sections shows here."
							: "Change the filters to see more."
					}
				/>
			) : (
				<>
					<Table
						label="Everything"
						columns={columnsFor(search)}
						rows={rows}
						rowKey={(row) => row.id}
						sort={sort}
						onSort={(next) => show({ page }, { by: next })}
					/>
					<Paging
						from={from + 1}
						to={from + rows.length}
						total={shown.length}
						hasPrevious={page > 1}
						hasNext={page < pages}
						onPage={(direction) =>
							show({ page: direction === "next" ? page + 1 : page - 1 })
						}
					/>
				</>
			)}
		</div>
	);
}

/** The columns, each entry linking to its page with the list's address
 * carried along, so the page's way back keeps the filters. */
function columnsFor(search: string): TableColumn<Row>[] {
	return [
		{
			key: "entry",
			header: "Entry",
			priority: 1,
			cell: (row) => (
				<Link className={styles.entry} href={`${itemPath(row.id)}${search}`}>
					{row.content}
				</Link>
			),
		},
		{
			key: "kind",
			header: "Kind",
			priority: 2,
			cell: (row) => <Badge>{kindWord(row.kind)}</Badge>,
			fold: (row) => kindWord(row.kind),
		},
		{
			key: "about",
			header: "About",
			priority: 3,
			cell: (row) => (
				<SubjectBadges topics={row.topics} entities={row.entities} />
			),
		},
		{
			key: "last-supported",
			header: "Last supported",
			priority: 2,
			numeric: true,
			sortable: true,
			cell: (row) => (
				<time dateTime={row.last_supported_at}>
					{dayOf(row.last_supported_at)}
				</time>
			),
			fold: (row) => dayOf(row.last_supported_at),
		},
		{
			key: "state",
			header: "State",
			priority: 1,
			cell: (row) => {
				const state = stateOf(row);
				return <Status tone={state.tone}>{state.word}</Status>;
			},
		},
	];
}

/** The filter row: Kind, State and Subject, each not at its default also
 * a pill that puts it back, and the count. */
function FilterBar({
	filters,
	total,
	onChange,
}: {
	filters: Filters;
	total: number;
	onChange: (next: Partial<Filters>, options?: { replace?: boolean }) => void;
}) {
	const topics = useCanonicalTopics();
	const entities = useEntities();
	const { subject } = filters;
	const subjectName =
		subject?.kind === "topic"
			? (topics.data?.find(({ slug }) => slug === subject.slug)?.name ??
				subject.slug)
			: subject?.name;

	const pills: { label: string; reset: Partial<Filters> }[] = [];
	if (filters.kind !== "") {
		pills.push({
			label: `Kind: ${kindWord(filters.kind)}`,
			reset: { kind: DEFAULTS.kind },
		});
	}
	if (filters.state !== DEFAULTS.state) {
		const word = STATE_FILTERS.find(({ value }) => value === filters.state);
		pills.push({
			label: `State: ${word?.word}`,
			reset: { state: DEFAULTS.state },
		});
	}
	if (filters.subject !== null) {
		pills.push({
			label: `Subject: ${subjectName}`,
			reset: { subject: DEFAULTS.subject },
		});
	}

	return (
		<FilterRow
			role="search"
			aria-label="Filter everything"
			count={count(total)}
		>
			<Select
				label="Kind"
				value={filters.kind}
				onChange={(event) =>
					onChange({ kind: event.target.value as Kind | "" })
				}
			>
				<option value="">Any</option>
				{KINDS.map((kind) => (
					<option key={kind.value} value={kind.value}>
						{kind.word}
					</option>
				))}
			</Select>
			<Select
				label="State"
				value={filters.state}
				onChange={(event) =>
					onChange({ state: event.target.value as StateFilter })
				}
			>
				{STATE_FILTERS.map((state) => (
					<option key={state.value} value={state.value}>
						{state.word}
					</option>
				))}
			</Select>
			<Select
				label="Subject"
				value={subjectValue(filters.subject)}
				onChange={(event) =>
					onChange({ subject: subjectFrom(event.target.value) })
				}
			>
				<option value="">Any</option>
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
			{pills.map((pill) => (
				<FilterPill
					key={pill.label}
					label={pill.label}
					onRemove={() => onChange(pill.reset)}
				/>
			))}
		</FilterRow>
	);
}
