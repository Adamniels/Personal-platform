import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Button, type ButtonKind } from "../index";
import styles from "./button.module.css";

afterEach(cleanup);

const kinds: ButtonKind[] = ["primary", "secondary", "ghost", "destructive"];

describe("the button renders each kind and passes its props through", () => {
	it.each(kinds)(
		"renders %s as a real button with that kind's class",
		(kind) => {
			render(<Button kind={kind}>Save</Button>);
			const button = screen.getByRole("button", { name: "Save" });

			expect(button.tagName).toBe("BUTTON");
			expect(button.classList.contains(styles.button as string)).toBe(true);
			expect(button.classList.contains(styles[kind] as string)).toBe(true);
			for (const other of kinds.filter((k) => k !== kind)) {
				expect(button.classList.contains(styles[other] as string)).toBe(false);
			}
		},
	);

	it("is secondary by default", () => {
		render(<Button>Save</Button>);
		const button = screen.getByRole("button", { name: "Save" });

		expect(button.classList.contains(styles.secondary as string)).toBe(true);
	});

	it("renders disabled as a native disabled button that does not click", () => {
		const onClick = vi.fn();
		render(
			<Button kind="primary" disabled onClick={onClick}>
				Save
			</Button>,
		);
		const button = screen.getByRole("button", { name: "Save" });

		expect(button).toHaveProperty("disabled", true);
		fireEvent.click(button);
		expect(onClick).not.toHaveBeenCalled();
	});

	it("defaults type to button, so it never submits a form by accident", () => {
		render(<Button>Save</Button>);

		expect(screen.getByRole("button").getAttribute("type")).toBe("button");
	});

	it("forwards type, onClick and aria-label", () => {
		const onClick = vi.fn();
		render(
			<Button type="submit" onClick={onClick} aria-label="Close">
				x
			</Button>,
		);
		const button = screen.getByRole("button", { name: "Close" });

		expect(button.getAttribute("type")).toBe("submit");
		fireEvent.click(button);
		expect(onClick).toHaveBeenCalledOnce();
	});

	it("keeps a class it is given beside its own", () => {
		render(<Button className="extra">Save</Button>);
		const button = screen.getByRole("button");

		expect(button.classList.contains("extra")).toBe(true);
		expect(button.classList.contains(styles.button as string)).toBe(true);
	});
});
