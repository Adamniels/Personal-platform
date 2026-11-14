// Review's sections, in the order of their tabs, each with its own address. What each one holds
// arrives with its own slice; until then each says it is empty.

export type Section = {
	/** The tab's value. */
	key: string;
	label: string;
	/** The address a tab goes to. */
	path: string;
	/** Other addresses that show the section, as /review/study shows
	 * Study, the address of a study session from a later slice. */
	aliases?: readonly string[];
	/** What the section says until its slice lands. */
	empty: string;
};

export const SECTIONS: readonly Section[] = [
	{
		key: "study",
		label: "Study",
		path: "/review",
		aliases: ["/review/study"],
		empty: "Nothing to study yet",
	},
	{
		key: "cards",
		label: "Cards",
		path: "/review/cards",
		empty: "No cards yet",
	},
	{
		key: "decks",
		label: "Decks",
		path: "/review/decks",
		empty: "No decks yet",
	},
];

/** The section an address names, a trailing slash aside, or undefined when
 * it names none. */
export function sectionAt(pathname: string): Section | undefined {
	const path = pathname.replace(/(?<=.)\/+$/, "");
	return SECTIONS.find(
		(section) => section.path === path || section.aliases?.includes(path),
	);
}
