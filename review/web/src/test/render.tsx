// Renders the real page at an address, over the fake platform's review and brain, the way a test of
// a section reads it: through the address, the tabs and the section, never a component alone.
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { App } from "../app";
import { Providers } from "../providers";
import type { FakePlatform } from "./fake-platform";

// jsdom has HTMLDialogElement and its `open` attribute, but neither showModal
// nor close. These stand in for the browser's, as design/ui's dialog tests
// do: showModal opens the element and moves focus to its first button, and
// close closes it and fires `close`. The top layer, the backdrop and Escape
// are the browser's own, and tested in e2e/tests/test_gallery.py.
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
	this.open = true;
	this.querySelector("button")?.focus();
};
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
	if (!this.open) return;
	this.open = false;
	this.dispatchEvent(new Event("close"));
};

/** Opens the page at the path, over the platform, with the clock at `now`. */
export function openAt(
	path: string,
	platform: FakePlatform,
	{ now = Date.UTC(2026, 10, 14, 9, 0) }: { now?: number } = {},
) {
	window.history.pushState(null, "", path);
	return render(
		<Providers clients={platform.clients()} now={() => now}>
			<App />
		</Providers>,
	);
}

/** What assistive technology reads as an element's description: the text of
 * every element its aria-describedby names, in order. */
export function descriptionOf(element: Element): string {
	return (element.getAttribute("aria-describedby") ?? "")
		.split(/\s+/)
		.filter(Boolean)
		.map((id) => document.getElementById(id)?.textContent ?? "")
		.join(" ")
		.trim();
}

/** Sets a field's value as typing would. */
export function type(field: Element, value: string) {
	fireEvent.change(field, { target: { value } });
}

/** Lets every pending answer of the fake platform settle. */
export async function settle() {
	await act(() => new Promise((resolve) => setTimeout(resolve, 0)));
}

/** The form the page names, such as "New card". */
export function form(name: string) {
	return screen.getByRole("form", { name });
}

/** The topic picker's field inside a form, and what it lists. */
export function picker(inside: HTMLElement) {
	const field = within(inside).getByRole("combobox", { name: "Topics" });
	return {
		field,
		options: () =>
			within(inside)
				.queryAllByRole("option")
				.map((option) => option.textContent),
		picked: () =>
			within(inside)
				.queryAllByRole("button", { name: /^Remove / })
				.map((button) =>
					(button.getAttribute("aria-label") ?? "").replace(/^Remove /, ""),
				),
	};
}
