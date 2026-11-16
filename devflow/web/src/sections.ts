// Dev flow's sections, in the order of their tabs, each with its own address, and each showing its
// own content. "Running now" and "Usage and history" join with the parts that build them, never as
// empty tabs. The addresses the service answers with the page are in
// src/DevFlow/Pages/PageEndpoints.cs, and the two change together.

export type Section = {
	/** The tab's value. */
	key: string;
	label: string;
	/** The address a tab goes to. */
	path: string;
};

export const SECTIONS: readonly Section[] = [
	{
		key: "waiting",
		label: "Waiting for you",
		path: "/devflow",
	},
	{
		key: "projects",
		label: "Projects",
		path: "/devflow/projects",
	},
];

/** The section an address names, a trailing slash aside, or undefined when
 * it names none. */
export function sectionAt(pathname: string): Section | undefined {
	const path = pathname.replace(/(?<=.)\/+$/, "");
	return SECTIONS.find((section) => section.path === path);
}
