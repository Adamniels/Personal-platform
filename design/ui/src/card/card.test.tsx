import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Button, Card, Status } from "../index";
import hiddenStyles from "../visually-hidden.module.css";
import styles from "./card.module.css";
import css from "./card.module.css?raw";

afterEach(cleanup);

/** The declarations of one rule in a stylesheet read as text. */
function rule(sheet: string, selector: string): string {
	const start = sheet.indexOf(`${selector} {`);
	expect(start, `no rule for ${selector}`).toBeGreaterThanOrEqual(0);
	return sheet.slice(start, sheet.indexOf("}", start));
}

describe("a card lays out its header, body and footer", () => {
	it("is an article named by its title, which is an h2", () => {
		render(<Card title="Review">One key fact</Card>);

		const card = screen.getByRole("article", { name: "Review" });
		const heading = screen.getByRole("heading", { name: "Review" });
		expect(heading.tagName).toBe("H2");
		expect(card.contains(heading)).toBe(true);
		expect(card.classList.contains(styles.card as string)).toBe(true);
	});

	it("puts the aside in the header, beside the title", () => {
		render(
			<Card title="Review" aside={<Status tone="done">Done</Status>}>
				Body
			</Card>,
		);

		const heading = screen.getByRole("heading", { name: "Review" });
		const header = heading.parentElement;
		expect(header?.classList.contains(styles.header as string)).toBe(true);
		expect(header?.contains(screen.getByText("Done"))).toBe(true);
	});

	it("puts the body between the header and the footer", () => {
		render(
			<Card title="Review" footer={<Button>One action</Button>}>
				One key fact
			</Card>,
		);

		const card = screen.getByRole("article");
		const parts = [...card.children];
		expect(parts).toHaveLength(3);
		expect(parts[0]?.classList.contains(styles.header as string)).toBe(true);
		expect(parts[1]?.textContent).toBe("One key fact");
		expect(parts[2]?.classList.contains(styles.footer as string)).toBe(true);
		expect(parts[2]?.contains(screen.getByRole("button"))).toBe(true);
	});

	it("pushes the footer to the bottom, so cards in a row align", () => {
		expect(rule(css, ".card")).toMatch(/flex-direction:\s*column/);
		expect(rule(css, ".footer")).toMatch(/margin-top:\s*auto/);
	});

	it("renders no footer when it has none", () => {
		render(<Card title="Review">Body</Card>);

		expect(screen.getByRole("article").querySelector(`.${styles.footer}`)).toBe(
			null,
		);
	});

	it("passes a native article's props through", () => {
		render(
			<Card title="Review" className="extra" data-testid="card">
				Body
			</Card>,
		);

		const card = screen.getByTestId("card");
		expect(card.classList.contains("extra")).toBe(true);
		expect(card.classList.contains(styles.card as string)).toBe(true);
	});
});

describe("a card that needs the user is marked for assistive technology as well as by its outline", () => {
	it("carries the outline's class and a visually hidden Needs you", () => {
		render(
			<Card title="Review" attention>
				Body
			</Card>,
		);

		const card = screen.getByRole("article", { name: "Review" });
		expect(card.classList.contains(styles.attention as string)).toBe(true);
		const mark = screen.getByText("Needs you");
		expect(card.contains(mark)).toBe(true);
		expect(mark.classList.contains(hiddenStyles.hidden as string)).toBe(true);
		expect(mark.getAttribute("aria-hidden")).toBe(null);
	});

	it("has neither without attention", () => {
		render(<Card title="Review">Body</Card>);

		const card = screen.getByRole("article", { name: "Review" });
		expect(card.classList.contains(styles.attention as string)).toBe(false);
		expect(screen.queryByText("Needs you")).toBe(null);
	});
});
