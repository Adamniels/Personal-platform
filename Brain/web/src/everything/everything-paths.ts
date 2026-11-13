// The Everything section's addresses: its own, and an item's page one level under it.

export const EVERYTHING_PATH = "/brain/everything";

/** An item's page, under the section's address. */
export function itemPath(id: string): string {
	return `${EVERYTHING_PATH}/${encodeURIComponent(id)}`;
}
