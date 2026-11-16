// Dev flow's sections, in the order of their tabs, each with its own address. A section whose
// content has not arrived yet says it is empty; Projects shows its own list. "Running now" and "Usage and
// history" join with the parts that build them, never as empty tabs. The addresses the service
// answers with the page are in src/DevFlow/Pages/PageEndpoints.cs, and the two change together.

export type Section = {
	/** The tab's value. */
	key: string;
	label: string;
	/** The address a tab goes to. */
	path: string;
	/** What the section says until its slice lands: what is empty, and the
	 * line under it. Left out once the section shows its own content. */
	empty?: { title: string; detail: string };
};

export const SECTIONS: readonly Section[] = [
	{
		key: "waiting",
		label: "Waiting for you",
		path: "/devflow",
		empty: {
			title: "Nothing waits for you",
			detail: "Connect a repository under Projects to see what waits for you.",
		},
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
