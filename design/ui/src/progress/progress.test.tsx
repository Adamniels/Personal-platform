import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Meter, Progress } from "../index";
import styles from "./progress.module.css";
import css from "./progress.module.css?raw";

afterEach(cleanup);

/** The fill inside a bar, the one element the bar holds. */
function fill(bar: HTMLElement): HTMLElement {
	const inner = bar.firstElementChild;
	expect(inner).toBeInstanceOf(HTMLElement);
	return inner as HTMLElement;
}

describe("a progress bar says its value", () => {
	it("is an image named by its label and its value", () => {
		render(<Progress value={62} label="Context used" />);
		const bar = screen.getByRole("img", { name: "Context used: 62%" });

		expect(bar.getAttribute("aria-label")).toBe("Context used: 62%");
		expect(fill(bar).style.width).toBe("62%");
	});

	it.each([
		[-20, 0],
		[0, 0],
		[100, 100],
		[140, 100],
		[Number.NaN, 0],
	])("clamps %s to %s", (value, clamped) => {
		render(<Progress value={value} label="Budget used" />);
		const bar = screen.getByRole("img", { name: `Budget used: ${clamped}%` });

		expect(fill(bar).style.width).toBe(`${clamped}%`);
	});

	it("says a fractional value rounded, and draws it exactly", () => {
		render(<Progress value={33.4} label="Done" />);
		const bar = screen.getByRole("img", { name: "Done: 33%" });

		expect(fill(bar).style.width).toBe("33.4%");
	});

	it("fills with the progress colour by default, and the budget one for budget", () => {
		render(
			<>
				<Progress value={10} label="Progress" />
				<Progress value={10} label="Budget" kind="budget" />
			</>,
		);
		const progress = fill(screen.getByRole("img", { name: "Progress: 10%" }));
		const budget = fill(screen.getByRole("img", { name: "Budget: 10%" }));

		expect(progress.classList.contains(styles.progress as string)).toBe(true);
		expect(budget.classList.contains(styles.budget as string)).toBe(true);
		expect(budget.classList.contains(styles.progress as string)).toBe(false);
	});
});

describe("a meter row shows a mono label and a mono value", () => {
	it("shows the short label, a bar and the value, each in mono", () => {
		render(<Meter label="CTX" value={62} description="Context used" />);
		const label = screen.getByText("CTX");
		const value = screen.getByText("62%");

		for (const text of [label, value]) {
			expect(text.classList.contains(styles.mono as string)).toBe(true);
		}
		expect(css).toMatch(/\.mono\s*\{[^}]*font-family:\s*var\(--font-mono\)/);
		expect(
			screen.getByRole("img", { name: "Context used: 62%" }),
		).toBeDefined();
	});

	it("is read once, as the bar, so the label and value are hidden", () => {
		render(<Meter label="BUD" value={30} kind="budget" />);

		expect(screen.getByText("BUD").getAttribute("aria-hidden")).toBe("true");
		expect(screen.getByText("30%").getAttribute("aria-hidden")).toBe("true");
		const bar = screen.getByRole("img", { name: "BUD: 30%" });
		expect(fill(bar).classList.contains(styles.budget as string)).toBe(true);
	});

	it("shows the clamped value", () => {
		render(<Meter label="CTX" value={120} />);

		expect(screen.getByText("100%")).toBeDefined();
	});
});
