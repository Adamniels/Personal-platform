import { useState } from "react";
import {
	Badge,
	Button,
	FilterPill,
	FilterRow,
	Paging,
	Select,
	Status,
	type StatusTone,
	Table,
	type TableColumn,
	type TableSort,
	TextField,
} from "../../src";
import { Note, Row, Section } from "./section";

type Session = {
	id: string;
	title: string;
	project: string;
	hours: number;
	status: { tone: StatusTone; word: string };
	notes: string;
};

const sessions: Session[] = [
	{
		id: "lab",
		title: "Draft the lab report",
		project: "Embedded",
		hours: 1.5,
		status: { tone: "done", word: "Done" },
		notes: "Timer chapter",
	},
	{
		id: "login",
		title: "Fix the flaky login test",
		project: "Platform",
		hours: 0.5,
		status: { tone: "done", word: "Done" },
		notes: "Cookie expiry",
	},
	{
		id: "thesis",
		title: "Outline the thesis chapter",
		project: "Studies",
		hours: 3,
		status: { tone: "running", word: "Editing" },
		notes: "Second draft",
	},
	{
		id: "gate",
		title: "Review the merge gate",
		project: "Platform",
		hours: 2,
		status: { tone: "needs-you", word: "Needs you" },
		notes: "Waits on CI",
	},
	{
		id: "exam",
		title: "Study for the exam",
		project: "Studies",
		hours: 2.5,
		status: { tone: "idle", word: "Idle" },
		notes: "Past papers",
	},
	{
		id: "reading",
		title: "Tidy the reading list",
		project: "Studies",
		hours: 0.5,
		status: { tone: "done", word: "Done" },
		notes: "",
	},
	{
		id: "slice",
		title: "Write the next slice",
		project: "Platform",
		hours: 1,
		status: { tone: "idle", word: "Idle" },
		notes: "Table",
	},
	{
		id: "tokens",
		title: "Write the token tests",
		project: "Platform",
		hours: 4,
		status: { tone: "failed", word: "Failed" },
		notes: "Two languages",
	},
];

const months = [
	"Jan",
	"Feb",
	"Mar",
	"Apr",
	"May",
	"Jun",
	"Jul",
	"Aug",
	"Sep",
	"Oct",
	"Nov",
	"Dec",
];

type Month = { project: string; hours: number[] };

const byMonth: Month[] = [
	{ project: "Platform", hours: [12, 18, 22, 9, 30, 41, 8, 0, 26, 33, 38, 4] },
	{ project: "Studies", hours: [40, 36, 44, 38, 20, 6, 0, 0, 35, 42, 39, 12] },
	{ project: "Embedded", hours: [0, 0, 4, 10, 16, 12, 0, 0, 8, 14, 20, 6] },
];

const monthColumns: TableColumn<Month>[] = [
	{ key: "project", header: "Project", cell: (row) => row.project },
	...months.map(
		(month, index): TableColumn<Month> => ({
			key: month,
			header: month,
			cell: (row) => row.hours[index] ?? 0,
			numeric: true,
		}),
	),
];

const PAGE = 8;
const TOTAL = 214;

function sorted(rows: Session[], sort: TableSort): Session[] {
	const order = sort.direction === "ascending" ? 1 : -1;
	return [...rows].sort((a, b) =>
		sort.key === "hours"
			? (a.hours - b.hours) * order
			: a.title.localeCompare(b.title) * order,
	);
}

/** A table of eight rows with columns of all three priorities, one numeric
 * and one sorted, a selected and an editing row, the filter row with two
 * pills and paging; and a wide table that scrolls inside its wrapper. The
 * gallery is the caller, so it sorts, edits and pages the sample rows. */
export function TableSection() {
	const [rows, setRows] = useState(sessions);
	const [sort, setSort] = useState<TableSort>({
		key: "title",
		direction: "ascending",
	});
	const [editing, setEditing] = useState<string | undefined>("thesis");
	const [draft, setDraft] = useState("Outline the thesis chapter");
	const [pills, setPills] = useState(["Status: Done", "Updated: this week"]);
	const [page, setPage] = useState(0);

	const columns: TableColumn<Session>[] = [
		{
			key: "title",
			header: "Session",
			cell: (row) => row.title,
			sortable: true,
			edit: () => (
				<TextField
					label="Session"
					value={draft}
					onChange={(event) => setDraft(event.target.value)}
				/>
			),
		},
		{
			key: "project",
			header: "Project",
			cell: (row) => <Badge>{row.project}</Badge>,
			fold: (row) => row.project,
			priority: 2,
		},
		{
			key: "hours",
			header: "Hours",
			cell: (row) => row.hours.toFixed(1),
			fold: (row) => `${row.hours.toFixed(1)} h`,
			numeric: true,
			sortable: true,
			priority: 2,
			edit: (row) => (
				<TextField
					label="Hours"
					inputMode="decimal"
					defaultValue={row.hours.toFixed(1)}
				/>
			),
		},
		{
			key: "status",
			header: "Status",
			cell: (row) => <Status tone={row.status.tone}>{row.status.word}</Status>,
		},
		{
			key: "notes",
			header: "Notes",
			cell: (row) => row.notes,
			priority: 3,
		},
	];

	const from = page * PAGE + 1;
	const to = Math.min(from + PAGE - 1, TOTAL);

	return (
		<Section title="Table" wide>
			<Row label="Sessions, sorted, with a selected and an editing row" stack>
				<FilterRow
					search={
						<TextField label="Search sessions" placeholder="Search 214" />
					}
					count={`${PAGE} of ${TOTAL}`}
				>
					<Select label="Project">
						<option>All projects</option>
						<option>Platform</option>
						<option>Studies</option>
						<option>Embedded</option>
					</Select>
					{pills.map((pill) => (
						<FilterPill
							key={pill}
							label={pill}
							onRemove={() => setPills((all) => all.filter((p) => p !== pill))}
						/>
					))}
				</FilterRow>
				<Table
					label="Sessions"
					columns={columns}
					rows={sorted(rows, sort)}
					rowKey={(row) => row.id}
					sort={sort}
					onSort={setSort}
					rowState={(row) =>
						row.id === editing
							? "editing"
							: row.id === "login"
								? "selected"
								: undefined
					}
					rowAction={(row) => (
						<Button
							kind="ghost"
							aria-label={`Edit ${row.title}`}
							onClick={() => {
								setDraft(row.title);
								setEditing(row.id);
							}}
						>
							Edit
						</Button>
					)}
					onSave={(row) => {
						setRows((all) =>
							all.map((r) => (r.id === row.id ? { ...r, title: draft } : r)),
						);
						setEditing(undefined);
					}}
					onCancel={() => setEditing(undefined)}
				/>
				<Paging
					from={from}
					to={to}
					total={TOTAL}
					hasPrevious={page > 0}
					hasNext={to < TOTAL}
					onPage={(direction) =>
						setPage((p) => (direction === "next" ? p + 1 : p - 1))
					}
				/>
			</Row>
			<Row label="A wide table" stack>
				<Table
					label="Hours by month"
					columns={monthColumns}
					rows={byMonth}
					rowKey={(row) => row.project}
				/>
			</Row>
			<Note>
				Session and Status are priority 1, Project and Hours priority 2, and
				Notes priority 3: below 640px the priority 2 values fold into the line
				under each session, and Notes is left out. The gallery sorts, edits and
				pages its sample rows itself, as a page would; every page shows the same
				eight.
			</Note>
		</Section>
	);
}
