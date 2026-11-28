// Dev flow's sections, in the order of their tabs, each with its own address, and each showing its
// own content, and a project's page, its settings page, and a card's page and a feature's page under
// it, which sit under the Projects tab at their own addresses. The addresses the service answers with
// the page are in src/DevFlow/Pages/PageEndpoints.cs, and the two change together.

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
		key: "running",
		label: "Running now",
		path: "/devflow/running",
	},
	{
		key: "projects",
		label: "Projects",
		path: "/devflow/projects",
	},
	{
		key: "usage",
		label: "Usage and history",
		path: "/devflow/usage",
	},
	{
		key: "machines",
		label: "Machines",
		path: "/devflow/machines",
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

const SETTINGS_PAGE =
	/^\/devflow\/projects\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/settings\/*$/i;

/** The id of the project whose settings page an address names, a trailing
 * slash aside, or undefined when it names none. */
export function settingsAt(pathname: string): string | undefined {
	return SETTINGS_PAGE.exec(pathname)?.[1]?.toLowerCase();
}

/** A project's settings page's address, under its project's. */
export function settingsPath(id: string): string {
	return `${projectPath(id)}/settings`;
}

const CARD_PAGE =
	/^\/devflow\/projects\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/cards\/([1-9][0-9]{0,9})\/*$/i;

/** The project and the number of the card whose page an address names, a
 * trailing slash aside, or undefined when it names none. */
export function cardAt(
	pathname: string,
): { project: string; number: number } | undefined {
	const match = CARD_PAGE.exec(pathname);
	if (!match?.[1] || !match[2]) return undefined;
	const number = Number(match[2]);
	return number <= 2_147_483_647
		? { project: match[1].toLowerCase(), number }
		: undefined;
}

/** A card's page's address, under its project's. */
export function cardPath(project: string, number: number): string {
	return `${projectPath(project)}/cards/${number}`;
}

const FEATURE_PAGE =
	/^\/devflow\/projects\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/features\/([1-9][0-9]{0,9})\/*$/i;

/** The project and the number of the feature whose page an address names, a
 * trailing slash aside, or undefined when it names none. */
export function featureAt(
	pathname: string,
): { project: string; number: number } | undefined {
	const match = FEATURE_PAGE.exec(pathname);
	if (!match?.[1] || !match[2]) return undefined;
	const number = Number(match[2]);
	return number <= 2_147_483_647
		? { project: match[1].toLowerCase(), number }
		: undefined;
}

/** A feature's page's address, under its project's. */
export function featurePath(project: string, number: number): string {
	return `${projectPath(project)}/features/${number}`;
}
