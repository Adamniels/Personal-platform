// Dev flow's sections, in the order of their tabs, each with its own address, and each showing its
// own content, and a project's page, which sits under the Projects tab at its own address. "Running now" and "Usage and history" join with the parts that build them, never as
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

const PROJECT_PAGE =
	/^\/devflow\/projects\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/*$/i;

/** The id of the project whose page an address names, a trailing slash
 * aside, or undefined when it names none. */
export function projectAt(pathname: string): string | undefined {
	return PROJECT_PAGE.exec(pathname)?.[1]?.toLowerCase();
}

/** A project's page's address. */
export function projectPath(id: string): string {
	return `/devflow/projects/${encodeURIComponent(id)}`;
}
