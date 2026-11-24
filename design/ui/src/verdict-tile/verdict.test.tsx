import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { VerdictTile, VerdictTiles } from "../index";
import statusStyles from "../status/status.module.css";
import styles from "./verdict-tile.module.css";

afterEach(cleanup);

function ThreeTiles() {
	return (
		<VerdictTiles aria-label="At a glance">
			<VerdictTile
				label="The check"
				tone="done"
				status="Passed"
				line="npm run check · exit 0"
			/>
			<VerdictTile
				label="The review"
				tone="done"
				status="Approved"
				line="on a3f9c12 · 0 blocking"
			/>
			<VerdictTile
				label="Decisions"
				tone="needs-you"
				status="2 for you"
				line="+3 it did not need you for"
			/>
		</VerdictTiles>
	);
}

describe("a verdict tile", () => {
	it("a tile shows its label, a status label and a mono line", () => {
		render(<ThreeTiles />);

		const list = screen.getByRole("list", { name: "At a glance" });
		expect(list.tagName).toBe("UL");
		expect(list.classList.contains(styles.tiles as string)).toBe(true);
		const tiles = within(list).getAllByRole("listitem");
		expect(tiles).toHaveLength(3);

		const check = within(tiles[0] as HTMLElement);
		expect(
			(tiles[0] as HTMLElement).classList.contains(styles.tile as string),
		).toBe(true);
		const label = check.getByText("The check");
		const status = check.getByText("Passed");
		const line = check.getByText("npm run check · exit 0");
		expect(label.classList.contains(styles.label as string)).toBe(true);
		// The status is the package's status label, in its tone.
		expect(status.classList.contains(statusStyles.status as string)).toBe(true);
		expect(status.classList.contains(statusStyles.done as string)).toBe(true);
		expect(line.classList.contains(styles.line as string)).toBe(true);
		// Read in that order: the label, its status, then the line.
		expect(label.compareDocumentPosition(status)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);
		expect(status.compareDocumentPosition(line)).toBe(
			Node.DOCUMENT_POSITION_FOLLOWING,
		);

		const decisions = within(tiles[2] as HTMLElement).getByText("2 for you");
		expect(
			decisions.classList.contains(statusStyles["needs-you"] as string),
		).toBe(true);
	});

	it("has no mono line unless it is given one", () => {
		render(
			<VerdictTiles>
				<VerdictTile label="The review" tone="idle" status="Not reviewed" />
			</VerdictTiles>,
		);

		expect(document.querySelector(`.${styles.line}`)).toBe(null);
	});

	it("passes what a ul and an li take through to them", () => {
		render(
			<VerdictTiles className="mine" data-testid="tiles">
				<VerdictTile
					label="The check"
					tone="failed"
					status="Failed"
					className="own"
					data-testid="tile"
				/>
			</VerdictTiles>,
		);

		const tiles = screen.getByTestId("tiles");
		expect(tiles.classList.contains("mine")).toBe(true);
		expect(tiles.classList.contains(styles.tiles as string)).toBe(true);
		const tile = screen.getByTestId("tile");
		expect(tile.classList.contains("own")).toBe(true);
		expect(tile.classList.contains(styles.tile as string)).toBe(true);
	});
});
