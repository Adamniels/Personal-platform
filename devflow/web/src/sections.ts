// Dev flow's sections, in the order of their tabs, each with its own address. What each one holds
// arrives with a later slice; until then each says it is empty. "Running now" and "Usage and
// history" join with the parts that build them, never as empty tabs. The addresses the service
// answers with the page are in src/DevFlow/Pages/PageEndpoints.cs, and the two change together.

export type Section = {
	/** The tab's value. */
	key: string;
	label: string;
	/** The address a tab goes to. */
	path: string;
	/** What the section says until its slice lands: what is empty, and the
	 * line under it. */
	empty: { title: string; detail: string };
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
		empty: {
			title: "No project connected yet",
			detail: "Connecting a repository comes with a later part of Dev flow.",
		},
	},
];

/** The section an address names, a trailing slash aside, or undefined when
 * it names none. */
export function sectionAt(pathname: string): Section | undefined {
	const path = pathname.replace(/(?<=.)\/+$/, "");
	return SECTIONS.find((section) => section.path === path);
}
