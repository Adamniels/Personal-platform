// The memory centre's sections, in the order of their tabs, each with its own address. What each
// one holds arrives with its own slice; until then each says it is empty.

export type Section = {
	/** The tab's value. */
	key: string;
	label: string;
	path: string;
	/** Whether an address one level under the section's is one of its pages,
	 * as a topic's page is under Knowledge and a decision's under History. */
	pages?: true;
};

export const SECTIONS: readonly Section[] = [
	{ key: "profile", label: "Profile", path: "/brain" },
	{ key: "rules", label: "Rules", path: "/brain/rules" },
	{
		key: "knowledge",
		label: "Knowledge",
		path: "/brain/knowledge",
		pages: true,
	},
	{ key: "now", label: "Now", path: "/brain/now" },
	{ key: "history", label: "History", path: "/brain/history", pages: true },
	{ key: "everything", label: "Everything", path: "/brain/everything" },
	{ key: "timeline", label: "Timeline", path: "/brain/timeline" },
	{ key: "ask", label: "Ask", path: "/brain/ask" },
];

function trimmed(pathname: string): string {
	return pathname.replace(/(?<=.)\/+$/, "");
}

/** The section an address names, itself or one of its pages, a trailing
 * slash aside, or undefined when it names none. */
export function sectionAt(pathname: string): Section | undefined {
	const path = trimmed(pathname);
	return SECTIONS.find(
		(section) =>
			section.path === path || pageAt(section, pathname) !== undefined,
	);
}

/** The page under a section an address names, a topic's slug under
 * Knowledge or a decision's id under History, or undefined for the section
 * itself or an address deeper than one level. */
export function pageAt(section: Section, pathname: string): string | undefined {
	const path = trimmed(pathname);
	if (!section.pages || !path.startsWith(`${section.path}/`)) return undefined;
	const page = path.slice(section.path.length + 1);
	if (page === "" || page.includes("/")) return undefined;
	try {
		return decodeURIComponent(page);
	} catch {
		return undefined;
	}
}
