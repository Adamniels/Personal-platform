import { describe, expect, test } from "vitest";
import { projectAt, settingsAt, settingsPath } from "./sections";

const ID = "0190a8f0-0000-7000-8000-000000000001";

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
