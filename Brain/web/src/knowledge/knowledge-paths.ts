// The Knowledge section's addresses: its own, and a topic's page one level under it.

export const KNOWLEDGE_PATH = "/brain/knowledge";

/** A topic's page, under the section's address. */
export function topicPath(slug: string): string {
	return `${KNOWLEDGE_PATH}/${encodeURIComponent(slug)}`;
}
