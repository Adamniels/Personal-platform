import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createRef } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Switch } from "../index";

afterEach(cleanup);

describe("a switch", () => {
	it("a switch is a checkbox with role switch that says on and off", () => {
		const onChange = vi.fn();
		render(
			<Switch
				label="Questions before starting"
				help="On: a card with questions waits for your answers."
				onChange={onChange}
			/>,
		);
		const control = screen.getByRole("switch", {
			name: "Questions before starting",
		});

		// A native checkbox, so it is a real form control, with the role that
		// has a screen reader say on and off rather than checked.
		expect(control.tagName).toBe("INPUT");
		expect(control.getAttribute("type")).toBe("checkbox");
		expect(control.getAttribute("role")).toBe("switch");
		expect(control.getAttribute("aria-describedby")).toBeTruthy();
		expect(
			document.getElementById(
				control.getAttribute("aria-describedby") as string,
			)?.textContent,
		).toBe("On: a card with questions waits for your answers.");

		// Off, then on from the label's text, the whole row being the target,
		// then off again.
		expect(screen.getByRole("switch", { checked: false })).toBe(control);
		fireEvent.click(screen.getByText("Questions before starting"));
		expect(screen.getByRole("switch", { checked: true })).toBe(control);
		fireEvent.click(control);
		expect(screen.getByRole("switch", { checked: false })).toBe(control);
		expect(onChange).toHaveBeenCalledTimes(2);
	});

	it("does not change when disabled", () => {
		const onChange = vi.fn();
		render(<Switch label="Sound" disabled onChange={onChange} />);

		fireEvent.click(screen.getByText("Sound"));

		expect(screen.getByRole("switch")).toHaveProperty("checked", false);
		expect(onChange).not.toHaveBeenCalled();
	});

	it("passes native props through and forwards its ref", () => {
		const ref = createRef<HTMLInputElement>();
		render(<Switch ref={ref} label="Sound" name="sound" defaultChecked />);
		const control = screen.getByRole("switch", { name: "Sound" });

		expect(ref.current).toBe(control);
		expect(control.getAttribute("name")).toBe("sound");
		expect(control).toHaveProperty("checked", true);
		// Without help, it is described by nothing.
		expect(control.getAttribute("aria-describedby")).toBeNull();
	});
});
