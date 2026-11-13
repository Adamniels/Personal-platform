import { ArrowDown, ArrowUp } from "lucide-react";
import { type ComponentProps, type ReactNode, useEffect, useRef } from "react";
import { Button } from "../button/button";
import hidden from "../visually-hidden.module.css";
import styles from "./table.module.css";

/** One column of a `Table`. */
export type TableColumn<Row> = {
	/** Unique among the columns; what `onSort` reports. */
	key: string;
	/** The header cell's content. */
	header: ReactNode;
	/** What the column shows for a row. */
	cell: (row: Row) => ReactNode;
	/** Whether the column shows on phone: 1, the default, always; 2 folded
	 * into the line under the first cell; 3 not at all. */
	priority?: 1 | 2 | 3;
	/** Right aligned, in the mono font with tabular figures. Hard rule 8 sets
	 * numbers, times, dates and ids in mono, so a column of any of them is
	 * numeric. */
	numeric?: boolean;
	/** Whether the header is a button that asks for this column's sort. */
	sortable?: boolean;
	/** The short text a priority 2 column gives the folded line on phone.
	 * Left out, the cell is used when it is text or a number, and the column
	 * is skipped when it is not. */
	fold?: (row: Row) => string;
	/** The field or fields shown in place of the cell while a row is editing.
	 * Left out, the cell is shown. */
	edit?: (row: Row) => ReactNode;
};

export type TableSortDirection = "ascending" | "descending";

/** Which column the rows are sorted by, and which way. */
export type TableSort = { key: string; direction: TableSortDirection };

/** What a row is doing: both are drawn alike and marked `aria-selected`. */
export type TableRowState = "selected" | "editing";

export type TableProps<Row> = Omit<ComponentProps<"section">, "children"> & {
	/** The table's accessible name, and the name of the wrapper that scrolls. */
	label: string;
	columns: readonly TableColumn<Row>[];
	/** The rows, shown in the order given: the caller sorts, filters and pages. */
	rows: readonly Row[];
	/** A stable key for a row. */
	rowKey: (row: Row) => string;
	/** How the rows are sorted, shown on the sorted header. */
	sort?: TableSort;
	/** Called when a sortable header is pressed, with its key and the
	 * direction it asks for: the other way when it is already sorted,
	 * ascending otherwise. Nothing changes until the caller passes `sort`. */
	onSort?: (sort: TableSort) => void;
	/** A row's state, set by the caller. */
	rowState?: (row: Row) => TableRowState | undefined;
	/** The one action at the end of a row, such as an Edit button. It is not
	 * shown while the row is editing, and focus comes back to it after. */
	rowAction?: (row: Row) => ReactNode;
	/** Called when Save under an editing row is pressed. The table holds no
	 * edited value: the fields are the caller's, and so is what they hold. */
	onSave?: (row: Row) => void;
	/** Called when Cancel under an editing row is pressed, or on Escape. */
	onCancel?: (row: Row) => void;
};

/** "Table" in section 4 of design/DESIGN.md: a real table in a wrapper that
 * scrolls sideways, rendering what the caller gives it and holding no data
 * logic of its own. Everything a `section` takes goes to the wrapper. */
export function Table<Row>({
	label,
	columns,
	rows,
	rowKey,
	sort,
	onSort,
	rowState,
	rowAction,
	onSave,
	onCancel,
	className,
	...rest
}: TableProps<Row>) {
	// The folded line goes under the first column that shows on phone.
	const first = columns.findIndex((column) => (column.priority ?? 1) === 1);
	const folded = columns.filter((column) => column.priority === 2);
	return (
		<section
			// A region that scrolls is named and reachable by the keyboard, so it
			// can be scrolled without a pointer, even with no control inside.
			// biome-ignore lint/a11y/noNoninteractiveTabindex: a scrolling region must be focusable to be scrolled from the keyboard.
			tabIndex={0}
			aria-label={label}
			{...rest}
			className={[styles.wrapper, className].filter(Boolean).join(" ")}
		>
			<table className={styles.table} aria-label={label}>
				<thead>
					<tr>
						{columns.map((column) => (
							<HeaderCell
								key={column.key}
								column={column}
								sort={sort}
								onSort={onSort}
							/>
						))}
						{rowAction && (
							<th scope="col" className={styles.action}>
								<span className={hidden.hidden}>Actions</span>
							</th>
						)}
					</tr>
				</thead>
				<tbody>
					{rows.map((row) => (
						<BodyRow
							key={rowKey(row)}
							row={row}
							columns={columns}
							first={first}
							folded={folded}
							state={rowState?.(row)}
							rowAction={rowAction}
							onSave={onSave}
							onCancel={onCancel}
						/>
					))}
				</tbody>
			</table>
		</section>
	);
}

/** What can take focus: the first field of an editing row, and the row's
 * action once it leaves editing. */
const FIELD = "input:not([type='hidden']), select, textarea";
const FOCUSABLE = `${FIELD}, button, a[href], [tabindex]`;

function BodyRow<Row>({
	row,
	columns,
	first,
	folded,
	state,
	rowAction,
	onSave,
	onCancel,
}: {
	row: Row;
	columns: readonly TableColumn<Row>[];
	/** The index of the first column that shows on phone. */
	first: number;
	folded: readonly TableColumn<Row>[];
	state: TableRowState | undefined;
	rowAction: TableProps<Row>["rowAction"];
	onSave: TableProps<Row>["onSave"];
	onCancel: TableProps<Row>["onCancel"];
}) {
	const ref = useRef<HTMLTableRowElement>(null);
	const editing = state === "editing";
	const wasEditing = useRef(editing);

	// Entering editing puts focus in the first field. Leaving it puts focus
	// back on the row's action, when focus was in the row or went with the
	// Save or Cancel that was pressed; focus the user moved elsewhere stays.
	// A row that is editing when it first renders takes no focus, so a page
	// does not jump on load.
	useEffect(() => {
		const element = ref.current;
		if (!element || editing === wasEditing.current) return;
		wasEditing.current = editing;
		if (editing) {
			element.querySelector<HTMLElement>(FIELD)?.focus();
			return;
		}
		const active = document.activeElement;
		if (
			active === null ||
			active === document.body ||
			element.contains(active)
		) {
			element
				.querySelector<HTMLElement>(`.${styles.action} :is(${FOCUSABLE})`)
				?.focus();
		}
	}, [editing]);

	return (
		<tr
			ref={ref}
			className={styles.row}
			aria-selected={state ? true : undefined}
			data-state={state}
			onKeyDown={
				editing
					? (event) => {
							if (event.key !== "Escape") return;
							event.preventDefault();
							onCancel?.(row);
						}
					: undefined
			}
		>
			{columns.map((column, index) => {
				const content =
					editing && column.edit ? column.edit(row) : column.cell(row);
				if (index !== first) {
					return (
						<td
							key={column.key}
							className={cellClass(column)}
							data-priority={column.priority ?? 1}
						>
							{content}
						</td>
					);
				}
				// The first cell holds the folded line, and while editing the
				// Save and Cancel directly under its field instead.
				const fold = editing ? undefined : foldLine(folded, row);
				return (
					<td
						key={column.key}
						className={cellClass(column)}
						data-priority={column.priority ?? 1}
					>
						{editing ? (
							<div className={styles.editing}>
								{content}
								<div className={styles.choices}>
									<Button kind="secondary" onClick={() => onSave?.(row)}>
										Save
									</Button>
									<Button kind="ghost" onClick={() => onCancel?.(row)}>
										Cancel
									</Button>
								</div>
							</div>
						) : fold ? (
							<>
								<div>{content}</div>
								<div className={styles.fold}>{fold}</div>
							</>
						) : (
							content
						)}
					</td>
				);
			})}
			{rowAction && (
				<td className={styles.action}>{editing ? null : rowAction(row)}</td>
			)}
		</tr>
	);
}

/** The priority 2 values of a row as one line, "Topic · 3 / 5 · 12 Nov",
 * or nothing when none of them has text. */
function foldLine<Row>(
	columns: readonly TableColumn<Row>[],
	row: Row,
): string | undefined {
	const parts = columns.flatMap((column) => {
		if (column.fold) return [column.fold(row)];
		const cell = column.cell(row);
		return typeof cell === "string" || typeof cell === "number"
			? [String(cell)]
			: [];
	});
	const line = parts.filter((part) => part !== "").join(" · ");
	return line || undefined;
}

function cellClass<Row>(column: TableColumn<Row>): string | undefined {
	return column.numeric ? styles.numeric : undefined;
}

function HeaderCell<Row>({
	column,
	sort,
	onSort,
}: {
	column: TableColumn<Row>;
	sort: TableSort | undefined;
	onSort: TableProps<Row>["onSort"];
}) {
	const direction =
		column.sortable && sort?.key === column.key ? sort.direction : undefined;
	const classes = [cellClass(column), direction && styles.sorted]
		.filter(Boolean)
		.join(" ");
	const Arrow = direction === "ascending" ? ArrowUp : ArrowDown;
	return (
		<th
			scope="col"
			className={classes || undefined}
			aria-sort={direction}
			data-priority={column.priority ?? 1}
		>
			{column.sortable ? (
				<button
					type="button"
					className={styles.sort}
					onClick={() =>
						onSort?.({
							key: column.key,
							direction: direction === "ascending" ? "descending" : "ascending",
						})
					}
				>
					{column.header}
					{direction && (
						<Arrow aria-hidden="true" size={14} className={styles.arrow} />
					)}
				</button>
			) : (
				column.header
			)}
		</th>
	);
}
