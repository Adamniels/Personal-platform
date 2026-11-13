import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FilterPill, FilterRow, Select, TextField } from "../index";
import styles from "./filters.module.css";

afterEach(cleanup);

describe("filters wrap, and a pill can be removed", () => {
	it("lays out the caller's fields, with the search growing and the count on the right", () => {
		render(
			<FilterRow
				data-testid="filters"
				search={<TextField label="Search sessions" />}
				count="7 of 214"
			>
				<Select label="Project">
					<option>All projects</option>
				</Select>
				<FilterPill label="Status: Done" onRemove={() => {}} />
			</FilterRow>,
		);

		const row = screen.getByTestId("filters");
		expect(row.classList.contains(styles.row as string)).toBe(true);
		const search = screen.getByRole("textbox", { name: "Search sessions" });
		const grow = search.closest(`.${styles.search}`);
		expect(grow).not.toBeNull();
		expect(
			row.contains(screen.getByRole("combobox", { name: "Project" })),
		).toBe(true);
		const count = screen.getByText("7 of 214");
		expect(count.classList.contains(styles.count as string)).toBe(true);
		// The count is last, so it sits on the right.
		expect(row.lastElementChild).toBe(count);
	});

	it("wraps, which its stylesheet says", async () => {
		const css = (await import("./filters.module.css?raw")).default as string;
		const rule = css.slice(css.indexOf(".row {"));
		expect(rule.slice(0, rule.indexOf("}"))).toContain("flex-wrap: wrap");
		const search = css.slice(css.indexOf(".search {"));
		expect(search.slice(0, search.indexOf("}"))).toContain("flex: 1 1 220px");
	});

	it("leaves out the search and the count when not given", () => {
		render(
			<FilterRow data-testid="filters">
				<FilterPill label="Status: Done" onRemove={() => {}} />
			</FilterRow>,
		);

		const row = screen.getByTestId("filters");
		expect(row.querySelector(`.${styles.search}`)).toBeNull();
		expect(row.querySelector(`.${styles.count}`)).toBeNull();
	});

	it("gives a pill a remove button named for its filter", () => {
		const onRemove = vi.fn();
		render(<FilterPill label="Updated: this week" onRemove={onRemove} />);

		const remove = screen.getByRole("button", {
			name: "Remove filter Updated: this week",
		});
		expect(remove.tagName).toBe("BUTTON");
		expect(remove.getAttribute("type")).toBe("button");
		const pill = screen
			.getByText("Updated: this week")
			.closest(`.${styles.pill}`);
		expect(pill?.contains(remove)).toBe(true);
		// The icon is not read; the label says what it does.
		expect(remove.querySelector("svg")?.getAttribute("aria-hidden")).toBe(
			"true",
		);

		fireEvent.click(remove);
		expect(onRemove).toHaveBeenCalledTimes(1);
	});
});
