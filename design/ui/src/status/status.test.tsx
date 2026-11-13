import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Badge, Chip, Status, type StatusTone } from "../index";
import styles from "./status.module.css";

afterEach(cleanup);

// The five statuses of "Badge and status label" in section 4 of
// design/DESIGN.md, each with a word the caller chose.
const tones: [StatusTone, string][] = [
	["running", "Working"],
	["needs-you", "Needs review"],
	["done", "Confirmed"],
	["failed", "Failed"],
	["idle", "Idle"],
];

describe("a status label is a dot and a word", () => {
	it.each(tones)("%s renders its word as text", (tone, word) => {
		render(<Status tone={tone}>{word}</Status>);
		const label = screen.getByText(word);

		expect(label.textContent).toBe(word);
		expect(label.classList.contains(styles.status as string)).toBe(true);
		expect(label.classList.contains(styles[tone] as string)).toBe(true);
		for (const [other] of tones.filter(([t]) => t !== tone)) {
			expect(label.classList.contains(styles[other] as string)).toBe(false);
		}
	});

	it.each(tones)("%s hides its dot from assistive technology", (tone, word) => {
		render(<Status tone={tone}>{word}</Status>);
		const dot = screen.getByText(word).querySelector(`.${styles.dot}`);

		expect(dot).not.toBeNull();
		expect(dot?.getAttribute("aria-hidden")).toBe("true");
		expect(dot?.textContent).toBe("");
	});

	it("passes a native span's props through", () => {
		render(
			<Status tone="done" className="extra" title="Finished at 12:40">
				Done
			</Status>,
		);
		const label = screen.getByText("Done");

		expect(label.tagName).toBe("SPAN");
		expect(label.classList.contains("extra")).toBe(true);
		expect(label.getAttribute("title")).toBe("Finished at 12:40");
	});
});

describe("a status label refuses a tone that is not one of the five", () => {
	it("is a type error, checked by tsc in the gate", () => {
		// @ts-expect-error: there are exactly five tones, and paused is not one.
		const paused = <Status tone="paused">Paused</Status>;
		// @ts-expect-error: a status is always a dot plus a word.
		const wordless = <Status tone="idle" />;

		expect(paused).toBeDefined();
		expect(wordless).toBeDefined();
	});
});

describe("a badge has no dot", () => {
	it("renders its text in the neutral fill, with nothing hidden beside it", () => {
		render(<Badge>Topic</Badge>);
		const badge = screen.getByText("Topic");

		expect(badge.tagName).toBe("SPAN");
		expect(badge.classList.contains(styles.badge as string)).toBe(true);
		expect(badge.querySelector(`.${styles.dot}`)).toBeNull();
		expect(badge.children).toHaveLength(0);
	});

	it("takes a count", () => {
		render(<Badge>{14}</Badge>);

		expect(screen.getByText("14").classList).toContain(styles.badge);
	});
});

describe("a tool chip is its text alone", () => {
	it("renders its text with the chip's class and no dot", () => {
		render(<Chip>Claude</Chip>);
		const chip = screen.getByText("Claude");

		expect(chip.tagName).toBe("SPAN");
		expect(chip.classList.contains(styles.chip as string)).toBe(true);
		expect(chip.children).toHaveLength(0);
	});
});
