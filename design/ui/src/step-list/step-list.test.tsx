import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Status, StepList, StepListItem } from "../index";
import hiddenStyles from "../visually-hidden.module.css";
import styles from "./step-list.module.css";
import css from "./step-list.module.css?raw";

afterEach(cleanup);

/** Three steps: one done, the current one needing the user, one to come. */
function ThreeSteps() {
	return (
		<StepList aria-label="Setting it up">
			<StepListItem state="done" title="The setup run">
				Pull request #7 merged.
			</StepListItem>
			<StepListItem
				state="current"
				title="The settings"
				aside={<Status tone="needs-you">Needs you</Status>}
			>
				<button type="button">Accept</button>
			</StepListItem>
			<StepListItem state="to-come" title="The check">
				A worker runs the check.
			</StepListItem>
		</StepList>
	);
}

/** The rule a selector's block holds in the component's CSS. */
const rule = (selector: string): string => {
	const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
	return css.match(new RegExp(`${escaped}\\s*\\{([^}]*)\\}`))?.[1] ?? "";
};

describe("a step list", () => {
	it("a step list numbers its steps and marks done, current and to come", () => {
		render(<ThreeSteps />);

		const list = screen.getByRole("list", { name: "Setting it up" });
		expect(list.tagName).toBe("OL");
		const [done, current, toCome] = within(list).getAllByRole("listitem") as [
			HTMLElement,
			HTMLElement,
			HTMLElement,
		];

		// Each circle: a check for the done one, its number for the others,
		// drawn, so hidden from assistive technology, which hears the state in
		// words and the position from the ordered list.
		const marks = [done, current, toCome].map(
			(step) => step.querySelector(`.${styles.mark}`) as HTMLElement,
		);
		for (const mark of marks) {
			expect(mark.getAttribute("aria-hidden")).toBe("true");
		}
		expect(marks[0]?.querySelector("svg")).not.toBeNull();
		expect(marks[0]?.textContent).toBe("");
		expect(marks[1]?.textContent).toBe("2");
		expect(marks[2]?.textContent).toBe("3");

		// The state is said, never colour alone: the current step is the
		// list's current, and the others say done and to come.
		expect(current.getAttribute("aria-current")).toBe("step");
		expect(done.getAttribute("aria-current")).toBeNull();
		expect(toCome.getAttribute("aria-current")).toBeNull();
		const said = (step: HTMLElement) =>
			step.querySelector(`.${hiddenStyles.hidden}`)?.textContent;
		expect(said(done)).toBe("Done: ");
		expect(said(toCome)).toBe("To come: ");
		expect(said(current)).toBeUndefined();

		// Each state's look, by its class: the circle's fill and the title.
		expect(done.dataset.state).toBe("done");
		expect(current.dataset.state).toBe("current");
		expect(toCome.dataset.state).toBe("to-come");

		// The title, and the current step's status label beside it, and each
		// step's content.
		expect(within(current).getByText("The settings")).toBeTruthy();
		expect(within(current).getByText("Needs you")).toBeTruthy();
		expect(
			within(current).getByRole("button", { name: "Accept" }),
		).toBeTruthy();
		expect(within(done).getByText("Pull request #7 merged.")).toBeTruthy();
		expect(within(toCome).getByText("A worker runs the check.")).toBeTruthy();
	});

	it("draws each state with the tokens section 4 names", () => {
		// A 32px circle.
		expect(rule(".mark")).toMatch(/width:\s*32px/);
		expect(rule(".mark")).toMatch(/height:\s*32px/);
		expect(rule(".mark")).toMatch(/border-radius:\s*var\(--radius-full\)/);
		// Done: ok-soft with the check in ok, the title at weight 600.
		expect(rule('[data-state="done"] .mark')).toMatch(
			/background:\s*var\(--ok-soft\)/,
		);
		expect(rule('[data-state="done"] .mark')).toMatch(/color:\s*var\(--ok\)/);
		expect(rule('[data-state="done"] .title')).toMatch(/font-weight:\s*600/);
		// Current: the accent with its number.
		expect(rule('[data-state="current"] .mark')).toMatch(
			/background:\s*var\(--accent\)/,
		);
		expect(rule('[data-state="current"] .mark')).toMatch(
			/color:\s*var\(--on-accent\)/,
		);
		// To come: surface-3, its title in text-2 and its line in text-3.
		expect(rule('[data-state="to-come"] .mark')).toMatch(
			/background:\s*var\(--surface-3\)/,
		);
		expect(rule('[data-state="to-come"] .title')).toMatch(
			/color:\s*var\(--text-2\)/,
		);
		expect(rule('[data-state="to-come"] .content')).toMatch(
			/color:\s*var\(--text-3\)/,
		);
	});

	it("passes what an ol takes through, and numbers from one", () => {
		render(
			<StepList aria-label="Two" className="mine" id="steps">
				<StepListItem state="current" title="First" />
				<StepListItem state="to-come" title="Second" />
			</StepList>,
		);

		const list = screen.getByRole("list", { name: "Two" });
		expect(list.id).toBe("steps");
		expect(list.classList.contains("mine")).toBe(true);
		expect(list.classList.contains(styles.list as string)).toBe(true);
		const numbers = within(list)
			.getAllByRole("listitem")
			.map((step) => step.querySelector(`.${styles.mark}`)?.textContent);
		expect(numbers).toEqual(["1", "2"]);
	});
});
