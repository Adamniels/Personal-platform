// The History section's addresses: its own, and a decision's page one level under it.

export const HISTORY_PATH = "/brain/history";

/** A decision's page, under the section's address. */
export function decisionPath(id: string): string {
	return `${HISTORY_PATH}/${encodeURIComponent(id)}`;
}
