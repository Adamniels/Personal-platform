import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Paging } from "../index";

afterEach(cleanup);

describe("paging says where it is and disables what it cannot do", () => {
	it("says which rows are shown, out of how many", () => {
		render(
			<Paging
				from={1}
				to={7}
				total={214}
				hasPrevious={false}
				hasNext
				onPage={() => {}}
			/>,
		);

		expect(screen.getByText("Rows 1 to 7 of 214")).toBeDefined();
		expect(screen.getByRole("navigation", { name: "Pages" })).toBeDefined();
	});

	it("disables Previous on the first page", () => {
		render(
			<Paging
				from={1}
				to={7}
				total={214}
				hasPrevious={false}
				hasNext
				onPage={() => {}}
			/>,
		);

		expect(screen.getByRole("button", { name: "Previous" })).toHaveProperty(
			"disabled",
			true,
		);
		expect(screen.getByRole("button", { name: "Next" })).toHaveProperty(
			"disabled",
			false,
		);
	});

	it("disables Next on the last page", () => {
		render(
			<Paging
				from={211}
				to={214}
				total={214}
				hasPrevious
				hasNext={false}
				onPage={() => {}}
			/>,
		);

		expect(screen.getByText("Rows 211 to 214 of 214")).toBeDefined();
		expect(screen.getByRole("button", { name: "Previous" })).toHaveProperty(
			"disabled",
			false,
		);
		expect(screen.getByRole("button", { name: "Next" })).toHaveProperty(
			"disabled",
			true,
		);
	});

	it("reports which was pressed", () => {
		const onPage = vi.fn();
		render(
			<Paging
				from={8}
				to={14}
				total={214}
				hasPrevious
				hasNext
				onPage={onPage}
			/>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Next" }));
		expect(onPage).toHaveBeenLastCalledWith("next");
		fireEvent.click(screen.getByRole("button", { name: "Previous" }));
		expect(onPage).toHaveBeenLastCalledWith("previous");
	});

	it("takes its own name where a page has more than one", () => {
		render(
			<Paging
				label="Session pages"
				from={1}
				to={7}
				total={7}
				hasPrevious={false}
				hasNext={false}
				onPage={() => {}}
			/>,
		);

		expect(
			screen.getByRole("navigation", { name: "Session pages" }),
		).toBeDefined();
	});
});
