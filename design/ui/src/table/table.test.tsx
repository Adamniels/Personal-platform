import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import buttonStyles from "../button/button.module.css";
import {
	Button,
	Table,
	type TableColumn,
	type TableRowState,
	TextField,
} from "../index";
import styles from "./table.module.css";

afterEach(cleanup);

type Session = {
	id: string;
	title: string;
	project: string;
	hours: number;
	date: string;
	notes: string;
};

const sessions: Session[] = [
	{
		id: "a",
		title: "Read the lab brief",
		project: "Embedded",
		hours: 1.5,
		date: "12 Nov",
		notes: "Timer chapter",
	},
	{
		id: "b",
		title: "Write the token tests",
		project: "Platform",
		hours: 3,
		date: "11 Nov",
		notes: "Two languages",
	},
	{
		id: "c",
		title: "Plan the next slice",
		project: "Platform",
		hours: 0.5,
		date: "10 Nov",
		notes: "",
	},
];

const columns: TableColumn<Session>[] = [
	{ key: "title", header: "Session", cell: (row) => row.title },
	{
		key: "project",
		header: "Project",
		cell: (row) => row.project,
		priority: 2,
	},
	{
		key: "hours",
		header: "Hours",
		cell: (row) => row.hours.toFixed(1),
		fold: (row) => `${row.hours.toFixed(1)} h`,
		numeric: true,
		priority: 2,
		sortable: true,
	},
	{
		key: "date",
		header: "Date",
		cell: (row) => row.date,
		priority: 2,
		sortable: true,
	},
	{ key: "notes", header: "Notes", cell: (row) => row.notes, priority: 3 },
];

const id = (row: Session) => row.id;

function headers() {
	return screen.getAllByRole("columnheader");
}

function header(name: string) {
	return screen.getByRole("columnheader", { name: new RegExp(`^${name}`) });
}

function bodyRows() {
	// The first row is the header row.
	return screen.getAllByRole("row").slice(1);
}

describe("a table renders its columns and rows from what the caller gives it", () => {
	it("is a real table with column headers and cells", () => {
		render(
			<Table label="Sessions" columns={columns} rows={sessions} rowKey={id} />,
		);

		const table = screen.getByRole("table", { name: "Sessions" });
		expect(table.tagName).toBe("TABLE");
		expect(table.querySelector("thead")).not.toBeNull();
		expect(headers().map((th) => th.textContent)).toEqual([
			"Session",
			"Project",
			"Hours",
			"Date",
			"Notes",
		]);
		for (const th of headers()) {
			expect(th.tagName).toBe("TH");
			expect(th.getAttribute("scope")).toBe("col");
		}
		expect(bodyRows()).toHaveLength(3);
		const first = bodyRows()[0];
		const cells = first?.querySelectorAll("td") ?? [];
		expect(cells).toHaveLength(5);
		expect(cells[0]?.textContent).toContain("Read the lab brief");
		expect(cells[2]?.textContent).toBe("1.5");
		expect(cells[4]?.textContent).toBe("Timer chapter");
	});

	it("renders the rows in the order given, holding no data logic", () => {
		const reversed = [...sessions].reverse();
		render(
			<Table label="Sessions" columns={columns} rows={reversed} rowKey={id} />,
		);

		const titles = bodyRows().map(
			(row) => row.querySelector("td")?.firstChild?.textContent,
		);
		expect(titles).toEqual(reversed.map((row) => row.title));
	});

	it("puts the wrapper's props on the wrapper, which scrolls", () => {
		render(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				className="extra"
				data-testid="wrapper"
			/>,
		);

		const wrapper = screen.getByTestId("wrapper");
		expect(wrapper.classList.contains(styles.wrapper as string)).toBe(true);
		expect(wrapper.classList.contains("extra")).toBe(true);
		expect(wrapper.contains(screen.getByRole("table"))).toBe(true);
	});
});

describe("a sortable header reports a click and says how it is sorted", () => {
	it("marks the sorted column alone with aria-sort", () => {
		const { rerender } = render(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				sort={{ key: "date", direction: "descending" }}
				onSort={() => {}}
			/>,
		);

		expect(header("Date").getAttribute("aria-sort")).toBe("descending");
		expect(header("Date").classList.contains(styles.sorted as string)).toBe(
			true,
		);
		for (const name of ["Session", "Project", "Hours", "Notes"]) {
			expect(header(name).hasAttribute("aria-sort")).toBe(false);
			expect(header(name).classList.contains(styles.sorted as string)).toBe(
				false,
			);
		}

		rerender(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				sort={{ key: "hours", direction: "ascending" }}
				onSort={() => {}}
			/>,
		);
		expect(header("Hours").getAttribute("aria-sort")).toBe("ascending");
		expect(header("Date").hasAttribute("aria-sort")).toBe(false);
	});

	it("makes a sortable header a button, and leaves the others text", () => {
		render(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				onSort={() => {}}
			/>,
		);

		for (const name of ["Hours", "Date"]) {
			const button = header(name).querySelector("button");
			expect(button).not.toBeNull();
			expect(button?.getAttribute("type")).toBe("button");
			expect(button?.textContent).toBe(name);
		}
		for (const name of ["Session", "Project", "Notes"]) {
			expect(header(name).querySelector("button")).toBeNull();
		}
	});

	it("reports the column and the next direction, and the caller sorts", () => {
		const onSort = vi.fn();
		const { rerender } = render(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				sort={{ key: "date", direction: "descending" }}
				onSort={onSort}
			/>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Date" }));
		expect(onSort).toHaveBeenLastCalledWith({
			key: "date",
			direction: "ascending",
		});
		fireEvent.click(screen.getByRole("button", { name: "Hours" }));
		expect(onSort).toHaveBeenLastCalledWith({
			key: "hours",
			direction: "ascending",
		});
		// Nothing changed until the caller passes a new sort back.
		expect(header("Date").getAttribute("aria-sort")).toBe("descending");
		const titles = () =>
			bodyRows().map((row) => row.querySelector("td")?.firstChild?.textContent);
		expect(titles()).toEqual(sessions.map((row) => row.title));

		rerender(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				sort={{ key: "date", direction: "ascending" }}
				onSort={onSort}
			/>,
		);
		fireEvent.click(screen.getByRole("button", { name: "Date" }));
		expect(onSort).toHaveBeenLastCalledWith({
			key: "date",
			direction: "descending",
		});
	});

	it("shows an arrow on the sorted header, hidden from assistive technology", () => {
		render(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				sort={{ key: "date", direction: "descending" }}
				onSort={() => {}}
			/>,
		);

		const arrow = header("Date").querySelector("svg");
		expect(arrow).not.toBeNull();
		expect(arrow?.getAttribute("aria-hidden")).toBe("true");
		expect(header("Hours").querySelector("svg")).toBeNull();
	});
});

describe("a numeric column is right aligned in the mono font", () => {
	it("gives a numeric column's header and cells the numeric class", () => {
		render(
			<Table label="Sessions" columns={columns} rows={sessions} rowKey={id} />,
		);

		const numeric = styles.numeric as string;
		expect(header("Hours").classList.contains(numeric)).toBe(true);
		expect(header("Date").classList.contains(numeric)).toBe(false);
		for (const row of bodyRows()) {
			const cells = row.querySelectorAll("td");
			expect(cells[2]?.classList.contains(numeric)).toBe(true);
			expect(cells[0]?.classList.contains(numeric)).toBe(false);
			expect(cells[3]?.classList.contains(numeric)).toBe(false);
		}
	});
});

describe("a row can be selected or in editing, and says so", () => {
	it("sets aria-selected on a selected or editing row, and on no other", () => {
		const state = (row: Session): TableRowState | undefined =>
			row.id === "a" ? "selected" : row.id === "b" ? "editing" : undefined;
		render(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				rowState={state}
			/>,
		);

		const [selected, editing, plain] = bodyRows();
		expect(selected?.getAttribute("aria-selected")).toBe("true");
		expect(selected?.getAttribute("data-state")).toBe("selected");
		expect(editing?.getAttribute("aria-selected")).toBe("true");
		expect(editing?.getAttribute("data-state")).toBe("editing");
		expect(plain?.hasAttribute("aria-selected")).toBe(false);
		expect(plain?.hasAttribute("data-state")).toBe(false);
	});

	it("gives every row one action at its end", () => {
		render(
			<Table
				label="Sessions"
				columns={columns}
				rows={sessions}
				rowKey={id}
				rowAction={(row) => (
					<Button kind="ghost" aria-label={`Edit ${row.title}`}>
						Edit
					</Button>
				)}
			/>,
		);

		// The action column is named for assistive technology.
		expect(headers().at(-1)?.textContent).toBe("Actions");
		for (const [index, row] of bodyRows().entries()) {
			const last = row.querySelectorAll("td")[5];
			expect(last?.querySelector("button")?.getAttribute("aria-label")).toBe(
				`Edit ${sessions[index]?.title}`,
			);
		}
	});
});

describe("below 640px only priority one columns are columns", () => {
	it("gives each cell and header its column's priority", () => {
		render(
			<Table label="Sessions" columns={columns} rows={sessions} rowKey={id} />,
		);

		const expected = ["1", "2", "2", "2", "3"];
		expect(headers().map((th) => th.getAttribute("data-priority"))).toEqual(
			expected,
		);
		for (const row of bodyRows()) {
			const cells = [...row.querySelectorAll("td")];
			expect(cells.map((td) => td.getAttribute("data-priority"))).toEqual(
				expected,
			);
		}
	});

	it("folds the priority 2 values into one mono line under the first cell", () => {
		render(
			<Table label="Sessions" columns={columns} rows={sessions} rowKey={id} />,
		);

		const first = bodyRows()[0]?.querySelector("td");
		const fold = first?.querySelector(`.${styles.fold}`);
		// A text cell folds as itself; a column with `fold` uses it.
		expect(fold?.textContent).toBe("Embedded · 1.5 h · 12 Nov");
		// Priority 3 values are not folded.
		expect(fold?.textContent).not.toContain("Timer chapter");
		// Only the first cell holds the line.
		expect(bodyRows()[0]?.querySelectorAll(`.${styles.fold}`)).toHaveLength(1);
	});

	it("leaves out a value that is not text and has no fold, and an empty one", () => {
		const withStatus: TableColumn<Session>[] = [
			{ key: "title", header: "Session", cell: (row) => row.title },
			{
				key: "badge",
				header: "Badge",
				cell: (row) => <strong>{row.project}</strong>,
				priority: 2,
			},
			{ key: "notes", header: "Notes", cell: (row) => row.notes, priority: 2 },
			{ key: "date", header: "Date", cell: (row) => row.date, priority: 2 },
		];
		render(
			<Table
				label="Sessions"
				columns={withStatus}
				rows={sessions}
				rowKey={id}
			/>,
		);

		const third = bodyRows()[2]?.querySelector("td");
		expect(third?.querySelector(`.${styles.fold}`)?.textContent).toBe("10 Nov");
	});

	it("draws no line when there is nothing to fold", () => {
		render(
			<Table
				label="Sessions"
				columns={[columns[0] as TableColumn<Session>]}
				rows={sessions}
				rowKey={id}
			/>,
		);

		expect(document.querySelector(`.${styles.fold}`)).toBeNull();
	});
});

/** A table whose rows are edited in place, the edited title kept by the
 * harness, as a page keeps it. */
function EditingHarness({
	onSave,
	onCancel,
}: {
	onSave?: (row: Session, title: string) => void;
	onCancel?: (row: Session) => void;
}) {
	const [rows, setRows] = useState(sessions);
	const [editing, setEditing] = useState<string | undefined>();
	const [draft, setDraft] = useState("");
	const editable: TableColumn<Session>[] = columns.map((column) =>
		column.key === "title"
			? {
					...column,
					edit: () => (
						<TextField
							label="Session"
							value={draft}
							onChange={(event) => setDraft(event.target.value)}
						/>
					),
				}
			: column.key === "hours"
				? {
						...column,
						edit: (row) => (
							<TextField label="Hours" defaultValue={row.hours.toFixed(1)} />
						),
					}
				: column,
	);
	return (
		<>
			<Table
				label="Sessions"
				columns={editable}
				rows={rows}
				rowKey={id}
				rowState={(row) => (row.id === editing ? "editing" : undefined)}
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
					onSave?.(row, draft);
					setRows((all) =>
						all.map((r) => (r.id === row.id ? { ...r, title: draft } : r)),
					);
					setEditing(undefined);
				}}
				onCancel={(row) => {
					onCancel?.(row);
					setEditing(undefined);
				}}
			/>
			<button type="button">Elsewhere</button>
		</>
	);
}

function startEditing(title: string) {
	const action = screen.getByRole("button", { name: `Edit ${title}` });
	action.focus();
	fireEvent.click(action);
}

/** A row that stops editing from a button outside the table. */
function Outside() {
	const [state, setState] = useState<TableRowState | undefined>("editing");
	return (
		<>
			<Table
				label="Sessions"
				columns={columns.map((c) =>
					c.key === "title"
						? { ...c, edit: () => <TextField label="Session" /> }
						: c,
				)}
				rows={sessions.slice(0, 1)}
				rowKey={id}
				rowState={() => state}
				rowAction={() => <Button kind="ghost">Edit</Button>}
			/>
			<button type="button" onClick={() => setState(undefined)}>
				Stop
			</button>
		</>
	);
}

describe("an editing row shows the caller's fields in place of the cell, with Save and Cancel under them", () => {
	it("calls each column's edit in place of its cell, and keeps the others", () => {
		render(<EditingHarness />);
		startEditing("Write the token tests");

		const row = bodyRows()[1];
		const cells = row?.querySelectorAll("td") ?? [];
		const title = screen.getByRole("textbox", { name: "Session" });
		const hours = screen.getByRole("textbox", { name: "Hours" });
		expect(cells[0]?.contains(title)).toBe(true);
		expect((title as HTMLInputElement).value).toBe("Write the token tests");
		expect(cells[2]?.contains(hours)).toBe(true);
		expect((hours as HTMLInputElement).value).toBe("3.0");
		// A column without `edit` shows its cell.
		expect(cells[3]?.textContent).toBe("11 Nov");
		// Other rows are not editing.
		expect(screen.getAllByRole("textbox")).toHaveLength(2);
	});

	it("puts Save, secondary, and Cancel, ghost, directly under the first cell's field", () => {
		render(<EditingHarness />);
		startEditing("Write the token tests");

		const first = bodyRows()[1]?.querySelector("td");
		const save = screen.getByRole("button", { name: "Save" });
		const cancel = screen.getByRole("button", { name: "Cancel" });
		expect(first?.contains(save)).toBe(true);
		expect(first?.contains(cancel)).toBe(true);
		expect(save.classList.contains(buttonStyles.secondary as string)).toBe(
			true,
		);
		expect(cancel.classList.contains(buttonStyles.ghost as string)).toBe(true);
		// Under the field: the field comes first in the cell.
		const field = screen.getByRole("textbox", { name: "Session" });
		expect(
			field.compareDocumentPosition(save) & Node.DOCUMENT_POSITION_FOLLOWING,
		).toBeTruthy();
		// The editing row shows no action of its own while editing.
		expect(
			screen.queryByRole("button", { name: "Edit Write the token tests" }),
		).toBeNull();
	});

	it("reports Save and Cancel with the row, holding no edited value itself", () => {
		const onSave = vi.fn();
		const onCancel = vi.fn();
		render(<EditingHarness onSave={onSave} onCancel={onCancel} />);

		startEditing("Write the token tests");
		const field = screen.getByRole("textbox", { name: "Session" });
		fireEvent.change(field, { target: { value: "Write the parity tests" } });
		fireEvent.click(screen.getByRole("button", { name: "Save" }));
		expect(onSave).toHaveBeenCalledWith(sessions[1], "Write the parity tests");
		expect(
			screen.getByRole("button", { name: "Edit Write the parity tests" }),
		).toBeDefined();

		startEditing("Read the lab brief");
		fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
		expect(onCancel).toHaveBeenCalledWith(sessions[0]);
		expect(screen.queryByRole("textbox")).toBeNull();
	});

	it("cancels on Escape", () => {
		const onCancel = vi.fn();
		render(<EditingHarness onCancel={onCancel} />);
		startEditing("Read the lab brief");

		fireEvent.keyDown(screen.getByRole("textbox", { name: "Session" }), {
			key: "Escape",
		});

		expect(onCancel).toHaveBeenCalledWith(sessions[0]);
		expect(screen.queryByRole("textbox")).toBeNull();
	});

	it("moves focus to the first field on entering, and back to the row's action on leaving", () => {
		render(<EditingHarness />);

		startEditing("Read the lab brief");
		expect(document.activeElement).toBe(
			screen.getByRole("textbox", { name: "Session" }),
		);
		fireEvent.keyDown(document.activeElement as Element, { key: "Escape" });
		expect(document.activeElement).toBe(
			screen.getByRole("button", { name: "Edit Read the lab brief" }),
		);

		startEditing("Plan the next slice");
		fireEvent.click(screen.getByRole("button", { name: "Save" }));
		expect(document.activeElement).toBe(
			screen.getByRole("button", { name: "Edit Plan the next slice" }),
		);
	});

	it("leaves focus alone when it is elsewhere as the row leaves editing", () => {
		render(<Outside />);
		const stop = screen.getByRole("button", { name: "Stop" });
		stop.focus();
		fireEvent.click(stop);

		expect(document.activeElement).toBe(stop);
	});
});
