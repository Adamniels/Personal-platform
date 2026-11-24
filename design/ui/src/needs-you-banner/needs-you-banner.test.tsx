import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Button, NeedsYouBanner } from "../index";
import styles from "./needs-you-banner.module.css";

afterEach(cleanup);

describe("a banner shows its message and at most one action", () => {
	it("says Needs you, then its message, then its detail, in order", () => {
		render(
			<NeedsYouBanner
				message="No worker yet, so no card can run."
				detail="Make the project's key, then start a worker."
			/>,
		);

		const words = screen.getByText("Needs you");
		const message = screen.getByText("No worker yet, so no card can run.");
		const detail = screen.getByText(
			"Make the project's key, then start a worker.",
		);
		expect(words.classList.contains(styles.words as string)).toBe(true);
		expect(message.classList.contains(styles.message as string)).toBe(true);
		expect(detail.classList.contains(styles.detail as string)).toBe(true);
		expect(words.compareDocumentPosition(message)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(message.compareDocumentPosition(detail)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});

	it("draws its warning icon for the eye alone", () => {
		const { container } = render(<NeedsYouBanner message="Look at it." />);

		const icon = container.querySelector("svg");
		expect(icon?.getAttribute("aria-hidden")).toBe("true");
	});

	it("has no detail and no action unless it is given them", () => {
		const { container } = render(<NeedsYouBanner message="Look at it." />);

		expect(container.querySelector(`.${styles.detail}`)).toBe(null);
		expect(screen.queryByRole("button")).toBe(null);
	});

	it("renders its one action last", () => {
		render(
			<NeedsYouBanner
				message="Look at it."
				action={<Button>Open the project</Button>}
			/>,
		);

		const action = screen.getByRole("button", { name: "Open the project" });
		const root = screen.getByText("Needs you").closest(`.${styles.banner}`);
		expect(root?.lastElementChild?.contains(action)).toBe(true);
	});

	it("passes what a div takes through to its root", () => {
		render(
			<NeedsYouBanner
				message="Look at it."
				className="mine"
				data-testid="banner"
			/>,
		);

		const root = screen.getByTestId("banner");
		expect(root.classList.contains("mine")).toBe(true);
		expect(root.classList.contains(styles.banner as string)).toBe(true);
	});

	it("takes one element as its action, never a list, checked by tsc in the gate", () => {
		const two = (
			<NeedsYouBanner
				message="Look at it."
				// @ts-expect-error: at most one action, so not an array of them.
				action={[<Button key="a">A</Button>, <Button key="b">B</Button>]}
			/>
		);

		expect(two).toBeDefined();
	});
});
