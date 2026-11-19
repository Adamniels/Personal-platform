import {
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import { afterEach, expect, test } from "vitest";
import { cardScreenshot, Screenshots, waitingScreenshot } from "./screenshots";

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close. These stand in for the browser's, as design/ui's dialog tests
// do; the top layer, the backdrop and Escape are the browser's own, and
// tested in e2e/tests/test_gallery.py.
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
	this.open = true;
	this.querySelector("button")?.focus();
};
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
	if (!this.open) return;
	this.open = false;
	this.dispatchEvent(new Event("close"));
};

const ITEM = "00000000-0000-0000-0000-000000000304";
const BASE = `/devflow/api/waiting/${ITEM}/screenshots`;
const PROJECT = "11111111-1111-1111-1111-111111111111";

afterEach(cleanup);

/** The element at the index, which the test expects to be there. */
function nth(elements: HTMLElement[], index: number): HTMLElement {
	const found = elements[index];
	if (found === undefined) throw new Error(`no element at ${index}`);
	return found;
}

function row() {
	return within(screen.getByRole("list", { name: "Screenshots" }));
}

test("each pair shows as thumbnails under its name", () => {
	render(
		<Screenshots
			address={(name) => waitingScreenshot(ITEM, name)}
			groups={[
				{
					name: "devflow-item",
					images: ["devflow-item-desktop", "devflow-item-phone"],
				},
				{ name: "devflow-open", images: ["devflow-open-phone"] },
			]}
		/>,
	);

	const pairs = row().getAllByRole("listitem");
	expect(pairs).toHaveLength(2);
	expect(within(nth(pairs, 0)).getByText("devflow-item")).toBeTruthy();
	const images = within(nth(pairs, 0)).getAllByRole("img");
	expect(images.map((image) => image.getAttribute("alt"))).toEqual([
		"devflow-item-desktop",
		"devflow-item-phone",
	]);
	expect(images.map((image) => image.getAttribute("src"))).toEqual([
		`${BASE}/devflow-item-desktop`,
		`${BASE}/devflow-item-phone`,
	]);
	for (const image of images) {
		expect(image.getAttribute("loading")).toBe("lazy");
	}
	// A name with only one of the two shows that one.
	expect(within(nth(pairs, 1)).getByText("devflow-open")).toBeTruthy();
	expect(
		within(nth(pairs, 1))
			.getAllByRole("img")
			.map((image) => image.getAttribute("alt")),
	).toEqual(["devflow-open-phone"]);
	// Nothing is open yet.
	expect(screen.queryByRole("dialog")).toBeNull();
});

test("an item without screenshots shows nothing", () => {
	const { container } = render(
		<Screenshots
			address={(name) => waitingScreenshot(ITEM, name)}
			groups={[]}
		/>,
	);

	expect(container.innerHTML).toBe("");
	expect(screen.queryByText("Screenshots")).toBeNull();
});

test("a thumbnail opens the full image, titled by its name", () => {
	render(
		<Screenshots
			address={(name) => waitingScreenshot(ITEM, name)}
			groups={[
				{
					name: "devflow-item",
					images: ["devflow-item-desktop", "devflow-item-phone"],
				},
			]}
		/>,
	);

	fireEvent.click(row().getByRole("button", { name: "devflow-item-phone" }));

	const dialog = within(screen.getByRole("dialog"));
	expect(
		dialog.getByRole("heading", { name: "devflow-item-phone" }),
	).toBeTruthy();
	const full = dialog.getByRole("img", { name: "devflow-item-phone" });
	expect(full.getAttribute("src")).toBe(`${BASE}/devflow-item-phone`);
	// Shown to look at, so there is nothing to confirm: Close is its one button.
	expect(
		dialog.getAllByRole("button").map((button) => button.textContent),
	).toEqual(["Close"]);

	fireEvent.click(dialog.getByRole("button", { name: "Close" }));
	expect(screen.queryByRole("dialog")).toBeNull();
});

test("an image that fails to load shows its name and that it could not be loaded", () => {
	render(
		<Screenshots
			address={(name) => waitingScreenshot(ITEM, name)}
			groups={[
				{
					name: "devflow-item",
					images: ["devflow-item-desktop", "devflow-item-phone"],
				},
			]}
		/>,
	);

	fireEvent.error(row().getByRole("img", { name: "devflow-item-desktop" }));

	expect(row().queryByRole("img", { name: "devflow-item-desktop" })).toBeNull();
	expect(
		row().getByText("devflow-item-desktop could not be loaded"),
	).toBeTruthy();
	// The other one is still there.
	expect(row().getByRole("img", { name: "devflow-item-phone" })).toBeTruthy();
});

test("a name is put in the address as one segment", () => {
	render(
		<Screenshots
			address={(name) => waitingScreenshot(ITEM, name)}
			groups={[{ name: "odd", images: ["a/../b"] }]}
		/>,
	);

	expect(row().getByRole("img").getAttribute("src")).toBe(`${BASE}/a%2F..%2Fb`);
});

test("the worker's note shows as text, with or without pictures", () => {
	render(
		<Screenshots
			address={(name) => cardScreenshot(PROJECT, 4, name)}
			groups={[]}
			note="Screenshots could not be taken: <b>/habits</b> did not load"
		/>,
	);

	expect(
		screen.getByText(
			"Screenshots could not be taken: <b>/habits</b> did not load",
		),
	).toBeTruthy();
	expect(screen.queryByRole("list")).toBeNull();
	expect(screen.getByRole("heading", { name: "Screenshots" })).toBeTruthy();
});

test("a card's pictures are read from its own route, each part one segment", () => {
	render(
		<Screenshots
			titled={false}
			address={(name) => cardScreenshot(PROJECT, 4, name)}
			groups={[{ name: "habits", images: ["habits-desktop", "a/../b"] }]}
		/>,
	);

	// Untitled, inside a panel titled Screenshots, the list keeps its name.
	expect(screen.queryByRole("heading")).toBeNull();
	const images = row().getAllByRole("img");
	expect(images.map((image) => image.getAttribute("src"))).toEqual([
		`/devflow/api/projects/${PROJECT}/cards/4/screenshots/habits-desktop`,
		`/devflow/api/projects/${PROJECT}/cards/4/screenshots/a%2F..%2Fb`,
	]);
});
