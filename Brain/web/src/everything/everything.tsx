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
	TextField,
} from "@platform/ui";
import { type ReactNode, useEffect, useState } from "react";
import { navigate, usePathname, useSearch } from "../address";
import section from "../entries/section.module.css";
import { Link } from "../link";
import { pageAt, sectionAt } from "../sections";
import { SubjectBadges } from "../subjects/subject-fields";
import { useCanonicalTopics, useEntities } from "../subjects/subjects";
import styles from "./everything.module.css";
import { EVERYTHING_PATH, itemPath } from "./everything-paths";
import { Excerpt } from "./excerpt";
import {
	DEFAULTS,
	type Filters,
	filtersFrom,
	includesInactive,
	STATE_FILTERS,
	type StateFilter,
	type Subject,
	searchOf,
	subjectFrom,
	subjectValue,
} from "./filters";
import { ItemPage } from "./item-page";
import {
	type Row,
	SEARCH_LIMIT,
	useEverything,
	useSearchItems,
} from "./queries";
import { stateOf } from "./state";
import { dayOf, KINDS, type Kind, kindWord } from "./words";

/** How many rows a page holds. */
export const PAGE_SIZE = 50;

/** How long typing pauses before a search is asked for, in milliseconds. */
const SEARCH_PAUSE = 250;

/** The longest search the brain takes. */
const SEARCH_MAX = 200;

/** The section: an item's page when the address names one, the table
 * otherwise. */
export function Everything() {
	const pathname = usePathname();
	const here = sectionAt(pathname);
	const id = here && pageAt(here, pathname);
	return id === undefined ? <Overview /> : <ItemPage id={id} />;
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
	return (
		<div className={section.section}>
			<Listing
				filters={filters}
				sort={sort}
				search={search}
				everything={everything.data}
				onChange={show}
			/>
		</div>
	);
}

/** The filter row and what it shows: every item, or, with search words,
 * what a search finds, narrowed by the other filters and paged. The filter
 * row stays while a search loads, so typing goes on in its field. */
function Listing({
	filters,
	sort,
	search,
	everything,
	onChange,
}: {
	filters: Filters;
	sort: TableSort;
	search: string;
	everything: readonly Row[];
	onChange: (
		next: Partial<Filters>,
		options?: { replace?: boolean; by?: TableSort },
	) => void;
}) {
	// A search is asked for once typing pauses, not at every key.
	const words = useDebounced(filters.search.trim(), SEARCH_PAUSE);
	const found = useSearchItems(words, {
		kind: filters.kind,
		inactive: includesInactive(filters.state),
	});
	const searching = words !== "";
	const source = searching ? found.data : everything;

	const shown = sorted(
		(source ?? []).filter(
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

	let body: ReactNode;
	if (searching && found.isPending) {
		body = <p className={styles.note}>Searching…</p>;
	} else if (searching && found.isError) {
		body = (
			<EmptyState
				title="The search could not be made"
				detail="Change the words, or reload the page to try again."
			/>
		);
	} else if (rows.length === 0) {
		const empty = everything.length === 0 && !searching;
		body = (
			<EmptyState
				title={empty ? "Nothing in your memory yet" : "Nothing matches"}
				detail={
					empty
						? "What you add in the other sections shows here."
						: "Change the search or the filters to see more."
				}
			/>
		);
	} else {
		body = (
			<>
				<Table
					label="Everything"
					columns={columnsFor(search)}
					rows={rows}
					rowKey={(row) => row.id}
					sort={sort}
					onSort={(next) => onChange({}, { by: next })}
				/>
				<Paging
					from={from + 1}
					to={from + rows.length}
					total={shown.length}
					hasPrevious={page > 1}
					hasNext={page < pages}
					onPage={(direction) =>
						onChange({ page: direction === "next" ? page + 1 : page - 1 })
					}
				/>
				{searching && found.data?.length === SEARCH_LIMIT ? (
					<p className={styles.note}>
						A search shows the {SEARCH_LIMIT} entries supported most recently.
						Add a word to narrow it.
					</p>
				) : null}
			</>
		);
	}

	return (
		<>
			<FilterBar filters={filters} total={shown.length} onChange={onChange} />
			{body}
		</>
	);
}

/** The value, once it has stayed the same for `delay` milliseconds. */
function useDebounced<T>(value: T, delay: number): T {
	const [settled, setSettled] = useState(value);
	useEffect(() => {
		const timer = setTimeout(() => setSettled(value), delay);
		return () => clearTimeout(timer);
	}, [value, delay]);
	return settled;
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
				<>
					<Link className={styles.entry} href={`${itemPath(row.id)}${search}`}>
						{row.content}
					</Link>
					{row.excerpt ? (
						<Excerpt className={styles.excerpt} html={row.excerpt} />
					) : null}
				</>
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
	if (filters.search.trim() !== "") {
		pills.push({
			label: `Search: ${filters.search.trim()}`,
			reset: { search: DEFAULTS.search },
		});
	}
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
			search={
				<TextField
					label="Search"
					type="search"
					maxLength={SEARCH_MAX}
					value={filters.search}
					onChange={(event) =>
						onChange({ search: event.target.value }, { replace: true })
					}
				/>
			}
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
