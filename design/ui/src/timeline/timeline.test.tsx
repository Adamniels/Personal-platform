import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Timeline, TimelineEntry } from "../index";
import hiddenStyles from "../visually-hidden.module.css";
import styles from "./timeline.module.css";

afterEach(cleanup);

function ThreeEntries() {
	return (
		<Timeline aria-label="History">
			<TimelineEntry
				time="12 Nov 19:31"
				detail="Waiting for your review."
				attention
			>
				New topic: rate monotonic scheduling
			</TimelineEntry>
			<TimelineEntry time="10 Nov 21:05">
				Finished lesson 3: borrowing rules
			</TimelineEntry>
			<TimelineEntry time="9 Nov 08:12">Started the course</TimelineEntry>
		</Timeline>
	);
}

describe("a timeline is a list, and the last entry draws no line", () => {
	it("is an ordered list with one item per entry", () => {
		render(<ThreeEntries />);

		const list = screen.getByRole("list", { name: "History" });
		expect(list.tagName).toBe("OL");
		expect(within(list).getAllByRole("listitem")).toHaveLength(3);
	});

	it("draws a line under every entry but the last", () => {
		render(<ThreeEntries />);

		const lines = screen
			.getAllByRole("listitem")
			.map((item) => item.querySelectorAll(`.${styles.line}`).length);
		expect(lines).toEqual([1, 1, 0]);
	});

	it("draws no line under a timeline of one entry", () => {
		render(
			<Timeline>
				<TimelineEntry time="12 Nov">Only</TimelineEntry>
			</Timeline>,
		);

		expect(
			screen.getByRole("listitem").querySelector(`.${styles.line}`),
		).toBeNull();
	});

	it("hides the rail from assistive technology", () => {
		render(<ThreeEntries />);

		for (const item of screen.getAllByRole("listitem")) {
			const rail = item.querySelector(`.${styles.rail}`);
			expect(rail?.getAttribute("aria-hidden")).toBe("true");
			expect(rail?.textContent).toBe("");
		}
	});

	it("shows the time in the mono style, the line, and the detail in order", () => {
		render(<ThreeEntries />);

		const first = screen.getAllByRole("listitem")[0];
		if (!first) throw new Error("no entries");
		const time = within(first).getByText("12 Nov 19:31");
		const line = within(first).getByText(
			"New topic: rate monotonic scheduling",
		);
		const detail = within(first).getByText("Waiting for your review.");
		expect(time.classList.contains(styles.time as string)).toBe(true);
		expect(line.classList.contains(styles.body as string)).toBe(true);
		expect(detail.classList.contains(styles.detail as string)).toBe(true);
		expect(time.compareDocumentPosition(line)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(line.compareDocumentPosition(detail)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
	});

	it("renders no detail line when it has none", () => {
		render(<ThreeEntries />);

		const second = screen.getAllByRole("listitem")[1];
		expect(second?.querySelector(`.${styles.detail}`)).toBeNull();
	});

	it("marks an entry that needs the user by its dot and in words", () => {
		render(<ThreeEntries />);

		const [first, second] = screen.getAllByRole("listitem");
		expect(first?.classList.contains(styles.attention as string)).toBe(true);
		expect(second?.classList.contains(styles.attention as string)).toBe(false);
		const mark = within(first as HTMLElement).getByText("Needs you");
		expect(mark.classList.contains(hiddenStyles.hidden as string)).toBe(true);
		expect(within(second as HTMLElement).queryByText("Needs you")).toBeNull();
	});
});

describe("an entry with a title", () => {
	it("a timeline entry shows its mono time, its title with a status label and its body, the last with no line after it", () => {
		render(
			<Timeline aria-label="How it got here">
				<TimelineEntry
					time="20 Nov 14:02 · fodr-mini-1"
					title="Build"
					status={{ tone: "done", word: "Succeeded" }}
				>
					<p>Check exit 0 on 4c7d210</p>
					<ul aria-label="Findings">
						<li>The date ignores the locale</li>
					</ul>
				</TimelineEntry>
				<TimelineEntry
					time="Now"
					title="Now"
					status={{ tone: "needs-you", word: "Waiting for you" }}
					attention
				/>
			</Timeline>,
		);

		// An entry with a title is named by it, so a page finds it by role.
		const build = screen.getByRole("listitem", { name: "Build" });
		const time = within(build).getByText("20 Nov 14:02 · fodr-mini-1");
		const title = within(build).getByRole("heading", { name: "Build" });
		const status = within(build).getByText("Succeeded");
		const body = within(build).getByText("Check exit 0 on 4c7d210");
		expect(time.classList.contains(styles.time as string)).toBe(true);
		expect(title.classList.contains(styles.title as string)).toBe(true);
		// The status label sits on the title's line, after the title.
		expect(title.parentElement?.contains(status)).toBe(true);
		expect(time.compareDocumentPosition(title)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(title.compareDocumentPosition(status)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(status.compareDocumentPosition(body)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		// The body holds blocks, a list among them.
		expect(body.closest(`.${styles.body}`)).not.toBeNull();
		expect(within(build).getByRole("list", { name: "Findings" })).toBeTruthy();
		expect(build.querySelector(`.${styles.line}`)).not.toBeNull();

		// The last entry, a title and no body, draws no line.
		const now = screen.getByRole("listitem", { name: "Now" });
		expect(within(now).getByText("Waiting for you")).toBeTruthy();
		expect(now.querySelector(`.${styles.line}`)).toBeNull();
		expect(now.querySelector(`.${styles.body}`)).toBeNull();
	});
});
