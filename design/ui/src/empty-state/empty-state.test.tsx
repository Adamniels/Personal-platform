import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Button, EmptyState } from "../index";
import styles from "./empty-state.module.css";

afterEach(cleanup);

describe("an empty state has one line that says what is empty, one that says what happens next, and at most one action", () => {
	it("renders the two lines in order, each its own paragraph", () => {
		render(
			<EmptyState
				title="No cards due"
				detail="The next 6 cards come back tomorrow morning."
			/>,
		);

		const title = screen.getByText("No cards due");
		const detail = screen.getByText(
			"The next 6 cards come back tomorrow morning.",
		);
		expect(title.tagName).toBe("P");
		expect(detail.tagName).toBe("P");
		expect(title.classList.contains(styles.title as string)).toBe(true);
		expect(detail.classList.contains(styles.detail as string)).toBe(true);
		expect(title.compareDocumentPosition(detail)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});

	it("has no action unless it is given one", () => {
		render(<EmptyState title="Nothing here" detail="Add the first one." />);

		expect(screen.queryByRole("button")).toBe(null);
	});

	it("renders its one action after the lines", () => {
		render(
			<EmptyState
				title="No cards due"
				detail="They come back tomorrow."
				action={<Button>Review ahead</Button>}
			/>,
		);

		const action = screen.getByRole("button", { name: "Review ahead" });
		expect(screen.getAllByRole("button")).toHaveLength(1);
		const root = screen.getByText("No cards due").parentElement;
		expect(root?.lastElementChild?.contains(action)).toBe(true);
	});

	it("takes one element as its action, never a list, checked by tsc in the gate", () => {
		const two = (
			<EmptyState
				title="Empty"
				detail="Next"
				// @ts-expect-error: at most one action, so not an array of them.
				action={[<Button key="a">A</Button>, <Button key="b">B</Button>]}
			/>
		);

		expect(two).toBeDefined();
	});
});
