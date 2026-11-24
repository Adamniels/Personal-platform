// What the tests of "Waiting for you" share: choosing an item in the inbox's queue, as the owner
// does, to see its detail. Only tests import it.
import { fireEvent, screen, within } from "@testing-library/react";

const literal = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** The inbox's queue, once it has drawn. */
export async function queue(): Promise<HTMLElement> {
	return screen.findByRole("navigation", { name: "The queue" });
}

/** Chooses the waiting item titled `title` in the queue and answers its
 * detail, the item's card beside the queue. */
export async function choose(title: string): Promise<HTMLElement> {
	const row = await within(await queue()).findByRole("link", {
		name: new RegExp(`^${literal(title)} `),
	});
	fireEvent.click(row);
	return screen.findByRole("article", { name: title });
}

/** The tiles of an item's detail, "At a glance", each as its label, its
 * status's word and its mono line, in order. */
export function tiles(detail: HTMLElement): string[][] {
	const list = within(detail).getByRole("list", { name: "At a glance" });
	return within(list)
		.getAllByRole("listitem")
		.map((tile) =>
			Array.from(tile.children).map((part) => part.textContent ?? ""),
		);
}
