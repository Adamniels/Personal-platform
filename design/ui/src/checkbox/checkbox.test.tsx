import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createRef } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Checkbox } from "../index";
import styles from "./checkbox.module.css";

afterEach(cleanup);

describe("a checkbox row is one target", () => {
	it("is a native checkbox named by its label", () => {
		render(<Checkbox label="Write the tests" />);
		const box = screen.getByRole("checkbox", { name: "Write the tests" });

		expect(box.tagName).toBe("INPUT");
		expect(box.getAttribute("type")).toBe("checkbox");
	});

	it("checks the box when the label text is clicked", () => {
		const onChange = vi.fn();
		render(<Checkbox label="Write the tests" onChange={onChange} />);

		fireEvent.click(screen.getByText("Write the tests"));

		expect(screen.getByRole("checkbox")).toHaveProperty("checked", true);
		expect(onChange).toHaveBeenCalledOnce();
	});

	it("does not change when disabled", () => {
		const onChange = vi.fn();
		render(<Checkbox label="Write the tests" disabled onChange={onChange} />);

		fireEvent.click(screen.getByText("Write the tests"));

		expect(screen.getByRole("checkbox")).toHaveProperty("checked", false);
		expect(onChange).not.toHaveBeenCalled();
	});

	it("passes native props through and forwards its ref", () => {
		const ref = createRef<HTMLInputElement>();
		render(
			<Checkbox
				ref={ref}
				label="Done"
				name="done"
				value="yes"
				defaultChecked
				className="extra"
			/>,
		);
		const box = screen.getByRole("checkbox", { name: "Done" });

		expect(ref.current).toBe(box);
		expect(box.getAttribute("name")).toBe("done");
		expect(box.getAttribute("value")).toBe("yes");
		expect(box).toHaveProperty("checked", true);
		expect(box.classList.contains("extra")).toBe(true);
	});
});

describe("a done checkbox has the completed look", () => {
	it("marks the row done, and only when asked", () => {
		render(
			<>
				<Checkbox label="Open" />
				<Checkbox label="Finished" done defaultChecked />
			</>,
		);
		const row = (name: string) =>
			screen.getByRole("checkbox", { name }).closest("label");

		expect(row("Finished")?.classList.contains(styles.done as string)).toBe(
			true,
		);
		expect(row("Open")?.classList.contains(styles.done as string)).toBe(false);
	});
});
