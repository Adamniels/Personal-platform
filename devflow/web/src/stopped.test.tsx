import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import type { ActionsApi } from "./actions";
import { ApiError } from "./api/client";
import { App } from "./app";
import type { Status } from "./status";
import type { OwnItem, WaitingApi, WaitingList } from "./waiting";

const NOW = Date.parse("2026-11-17T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const LANDED = `ab12cd34${"0".repeat(32)}`;
const TITLE = "Something landed on main that Dev flow did not put there";

function stop(overrides: Partial<OwnItem> = {}): OwnItem {
	return {
		kind: "project_stopped",
		project_id: PROJECT,
		project_name: "Personal-platform",
		owner: "Adamniels",
		branch: "main",
		activity_type: "push",
		actor: "devflow-fake[bot]",
		commit: LANDED,
		commit_url: `https://github.com/Adamniels/Personal-platform/commit/${LANDED}`,
		landed_at: "2026-11-17T11:50:00+00:00",
		stopped_at: "2026-11-17T11:51:00+00:00",
		...overrides,
	};
}

function list(own: OwnItem[]): WaitingList {
	return {
		count: own.length,
		needs_relink: false,
		stopped_cards: [],
		failing_projects: [],
		items: [],
		projects: [
			{
				id: PROJECT,
				owner: "Adamniels",
				name: "Personal-platform",
				last_success_at: new Date(NOW - 60_000).toISOString(),
				stale: false,
				failing: false,
			},
		],
		own_items: own,
		cards: [],
		decisions: [],
	};
}

function status(): Status {
	return {
		code_host: "fake",
		github: { state: "linked", login: "octo-owner" },
	};
}

/** The page on the waiting tab with the stops, its list read again after a
 * resume answering `after`. */
function open(
	own: OwnItem[],
	{
		resume = () => Promise.resolve(),
		after = own,
	}: { resume?: ActionsApi["resume"]; after?: OwnItem[] } = {},
) {
	window.history.pushState(null, "", "/devflow");
	let answer = list(own);
	const loads = vi.fn(() => Promise.resolve(answer));
	const waiting: WaitingApi = {
		loadWaiting: loads,
		refresh: () => Promise.resolve(),
	};
	const resumes = vi.fn(async (id: string, commit: string) => {
		await resume(id, commit);
		answer = list(after);
	});
	render(
		<App
			loadStatus={() => Promise.resolve(status())}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
				disconnect: () => Promise.resolve(),
			}}
			waiting={waiting}
			actions={{
				merge: () => Promise.resolve(),
				sendBack: () => Promise.resolve(),
				resume: resumes,
				mergeCard: () => Promise.resolve(),
				sendBackCard: () => Promise.resolve(),
			}}
			now={() => NOW}
		/>,
	);
	return { resumes, loads };
}

async function card() {
	return within(
		await within(screen.getByRole("tabpanel")).findByRole("article", {
			name: TITLE,
		}),
	);
}

// jsdom has HTMLDialogElement but neither showModal nor close, as in
// actions.test.tsx.
beforeEach(() => {
	HTMLDialogElement.prototype.showModal = function showModal() {
		this.open = true;
	};
	HTMLDialogElement.prototype.close = function close() {
		this.open = false;
	};
});

afterEach(cleanup);

describe("a stopped project", () => {
	test("is under Stopped, counted, with the commit, who and a link", async () => {
		open([stop()]);

		const item = await card();
		const region = screen.getByRole("region", { name: /Stopped/ });
		expect(within(region).getByRole("article", { name: TITLE })).toBeTruthy();
		expect(screen.getByRole("tab", { name: "Waiting for you 1" })).toBeTruthy();
		expect(item.getByText("Stopped", { exact: true })).toBeTruthy();
		expect(item.getByText("Adamniels/Personal-platform")).toBeTruthy();
		expect(item.getByText("ab12cd3").tagName).toBe("CODE");
		expect(item.getByText("devflow-fake[bot]")).toBeTruthy();
		expect(item.getByText(/pushed by/)).toBeTruthy();
		const link = item.getByRole("link", { name: "Open the commit on GitHub" });
		expect(link.getAttribute("href")).toBe(stop().commit_url);
		expect(link.getAttribute("rel")).toBe("noreferrer");
		expect(link.getAttribute("target")).toBe("_blank");
	});

	test("says what landed in words, and nobody when GitHub named nobody", async () => {
		open([stop({ activity_type: "force_push", actor: null, branch: "trunk" })]);

		const item = within(
			await within(screen.getByRole("tabpanel")).findByRole("article", {
				name: "Something landed on trunk that Dev flow did not put there",
			}),
		);
		expect(
			item.getByText(/force pushed by an account GitHub no longer names/),
		).toBeTruthy();
	});

	test("a commit address that is not https is not linked", async () => {
		open([stop({ commit_url: "javascript:alert(1)" })]);

		const item = await card();
		expect(item.queryByRole("link")).toBeNull();
	});

	test("resuming asks first, and Cancel changes nothing", async () => {
		const { resumes } = open([stop()]);

		fireEvent.click(
			(await card()).getByRole("button", { name: "I have looked, resume" }),
		);
		const dialog = within(
			await screen.findByRole("dialog", {
				name: "Resume Adamniels/Personal-platform?",
			}),
		);
		expect(
			dialog.getByText(/Resuming accepts what landed on main/),
		).toBeTruthy();
		expect(dialog.getByText("ab12cd3").tagName).toBe("CODE");
		fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(resumes).not.toHaveBeenCalled();
		expect(await card()).toBeTruthy();
	});

	test("resuming sends the commit shown, and the item leaves", async () => {
		const { resumes, loads } = open([stop()], { after: [] });

		fireEvent.click(
			(await card()).getByRole("button", { name: "I have looked, resume" }),
		);
		const dialog = within(await screen.findByRole("dialog"));
		fireEvent.click(dialog.getByRole("button", { name: "Resume" }));

		await waitFor(() => expect(resumes).toHaveBeenCalledWith(PROJECT, LANDED));
		await waitFor(() =>
			expect(
				within(screen.getByRole("tabpanel")).queryByRole("article", {
					name: TITLE,
				}),
			).toBeNull(),
		);
		expect(loads.mock.calls.length).toBeGreaterThan(1);
		expect(
			await screen.findByText("Resumed Adamniels/Personal-platform"),
		).toBeTruthy();
	});

	test("a refused resume is said in the item, and it stays", async () => {
		open([stop()], {
			resume: () =>
				Promise.reject(
					new ApiError({
						status: 409,
						title: "Something else has landed since this was shown",
						detail: "Look at what is shown now before you resume.",
					}),
				),
		});

		fireEvent.click(
			(await card()).getByRole("button", { name: "I have looked, resume" }),
		);
		fireEvent.click(
			within(await screen.findByRole("dialog")).getByRole("button", {
				name: "Resume",
			}),
		);

		expect(await (await card()).findByRole("alert")).toHaveProperty(
			"textContent",
			"Something else has landed since this was shown. Look at what is shown now before you resume.",
		);
	});
});
