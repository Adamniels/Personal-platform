import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Button, Table, type TableColumn, type TableRowState } from "../index";
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
