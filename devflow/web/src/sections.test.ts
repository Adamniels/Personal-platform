import { describe, expect, test } from "vitest";
import {
	projectAt,
	SECTIONS,
	sectionAt,
	settingsAt,
	settingsPath,
} from "./sections";

const ID = "0190a8f0-0000-7000-8000-000000000001";

describe("the sections", () => {
	test("the tabs are Waiting for you, Running now, Projects, Usage and history, Machines", () => {
		expect(
			SECTIONS.map((section) => [section.key, section.label, section.path]),
		).toEqual([
			["waiting", "Waiting for you", "/devflow"],
			["running", "Running now", "/devflow/running"],
			["projects", "Projects", "/devflow/projects"],
			["usage", "Usage and history", "/devflow/usage"],
			["machines", "Machines", "/devflow/machines"],
		]);
		// Usage and history has its own address, its period in the query.
		expect(sectionAt("/devflow/usage")?.key).toBe("usage");
		expect(sectionAt("/devflow/usage/")?.key).toBe("usage");
		// Running now has its own address, a trailing slash aside.
		expect(sectionAt("/devflow/running")?.key).toBe("running");
		expect(sectionAt("/devflow/running/")?.key).toBe("running");
		expect(sectionAt("/devflow/running/more")).toBeUndefined();
	});
});

describe("a project's settings page has its own address", () => {
	test("settingsAt reads a settings address and settingsPath makes one", () => {
		expect(settingsPath(ID)).toBe(`/devflow/projects/${ID}/settings`);
		expect(settingsAt(settingsPath(ID))).toBe(ID);
		// A trailing slash aside, and the id in either case, read in lower case.
		expect(settingsAt(`/devflow/projects/${ID}/settings/`)).toBe(ID);
		expect(settingsAt(`/devflow/projects/${ID.toUpperCase()}/settings`)).toBe(
			ID,
		);

		// Anything else names no settings page.
		expect(settingsAt(`/devflow/projects/${ID}`)).toBeUndefined();
		expect(settingsAt(`/devflow/projects/${ID}/settings/more`)).toBeUndefined();
		expect(settingsAt("/devflow/projects/not-an-id/settings")).toBeUndefined();
		expect(settingsAt("/devflow/settings")).toBeUndefined();

		// And the project's own page is still only its own address.
		expect(projectAt(settingsPath(ID))).toBeUndefined();
	});
});
