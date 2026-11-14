// Review's sections, in the order of their tabs, each with its own address. What each one shows is
// in src/app.tsx.

export type Section = {
	/** The tab's value. */
	key: "study" | "cards" | "decks";
	label: string;
	/** The address a tab goes to. */
	path: string;
	/** Other addresses that show the section, as /review/study, a study
	 * session, shows Study. */
	aliases?: readonly string[];
};

export const SECTIONS: readonly Section[] = [
	{
		key: "study",
		label: "Study",
		path: "/review",
		aliases: ["/review/study"],
	},
	{
		key: "cards",
		label: "Cards",
		path: "/review/cards",
	},
	{
		key: "decks",
		label: "Decks",
		path: "/review/decks",
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
