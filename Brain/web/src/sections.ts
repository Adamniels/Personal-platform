// The memory centre's sections, in the order of their tabs, each with its own address. What each
// one holds arrives with its own slice; until then each says it is empty.

export type Section = {
	/** The tab's value. */
	key: string;
	label: string;
	path: string;
};

export const SECTIONS: readonly Section[] = [
	{ key: "profile", label: "Profile", path: "/brain" },
	{ key: "rules", label: "Rules", path: "/brain/rules" },
	{ key: "knowledge", label: "Knowledge", path: "/brain/knowledge" },
	{ key: "now", label: "Now", path: "/brain/now" },
	{ key: "history", label: "History", path: "/brain/history" },
	{ key: "everything", label: "Everything", path: "/brain/everything" },
	{ key: "timeline", label: "Timeline", path: "/brain/timeline" },
	{ key: "ask", label: "Ask", path: "/brain/ask" },
];

/** The section an address names, a trailing slash aside, or undefined when it
 * names none. */
export function sectionAt(pathname: string): Section | undefined {
	const path = pathname.replace(/(?<=.)\/+$/, "");
	return SECTIONS.find((section) => section.path === path);
}
