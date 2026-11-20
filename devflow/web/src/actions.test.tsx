import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import {
	afterAll,
	afterEach,
	beforeEach,
	describe,
	expect,
	test,
	vi,
} from "vitest";
import type { ActionsApi } from "./actions";
import { ApiError } from "./api/client";
import { App } from "./app";
import type { Status } from "./status";
import type { WaitingApi, WaitingItem, WaitingList } from "./waiting";

const NOW = Date.parse("2026-11-16T12:00:00Z");
const PROJECT = "11111111-1111-1111-1111-111111111111";
const HEAD = "4bf2a9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6";

function item(overrides: Partial<WaitingItem> = {}): WaitingItem {
	const number = overrides.number ?? 304;
	return {
		id: `00000000-0000-0000-0000-${String(number).padStart(12, "0")}`,
		project_id: PROJECT,
		project_name: "Personal-platform",
		number,
		kind: "waiting",
		sent_back: false,
		title: `Pull request ${number}`,
		web_url: `https://github.com/Adamniels/Personal-platform/pull/${number}`,
		slice_number: null,
		head_commit: HEAD,
		can_merge: true,
		summary: "Adds the vault.",
		reason: "High risk slice.",
		decisions: [],
		other_decisions: 0,
		reviewer_view: [],
		screenshots: [],
		read_at: new Date(NOW - 60_000).toISOString(),
		...overrides,
	};
}

function list(items: WaitingItem[]): WaitingList {
	return {
		count: items.filter((each) => !each.sent_back).length,
		needs_relink: false,
		stopped_cards: [],
		failing_projects: [],
		features: [],
		items,
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
		own_items: [],
		cards: [],
		decisions: [],
	};
}

function status(state: Status["github"]["state"] = "linked"): Status {
	return {
		code_host: "fake",
		github: { state, login: state === "none" ? null : "octo-owner" },
	};
}

type Opened = {
	actions: {
		merge: ReturnType<typeof vi.fn>;
		sendBack: ReturnType<typeof vi.fn>;
	};
	loads: ReturnType<typeof vi.fn>;
};

/** The page on the waiting tab, with the items, the link in the state, and
 * actions that resolve unless a test says otherwise. */
function open(
	items: WaitingItem[],
	{
		link = "linked",
		actions = {},
	}: { link?: Status["github"]["state"]; actions?: Partial<ActionsApi> } = {},
): Opened {
	window.history.pushState(null, "", "/devflow");
	const loads = vi.fn(() => Promise.resolve(list(items)));
	const waiting: WaitingApi = {
		loadWaiting: loads,
		refresh: () => Promise.resolve(),
	};
	const merge = vi.fn(actions.merge ?? (() => Promise.resolve()));
	const sendBack = vi.fn(actions.sendBack ?? (() => Promise.resolve()));
	render(
		<App
			loadStatus={() => Promise.resolve(status(link))}
			projects={{
				loadProjects: () => Promise.resolve([]),
				loadRepositories: () => new Promise(() => {}),
				connect: () => Promise.resolve(),
				disconnect: () => Promise.resolve(),
			}}
			waiting={waiting}
			actions={{
				merge,
				sendBack,
				resume: () => Promise.resolve(),
				mergeCard: () => Promise.resolve(),
				sendBackCard: () => Promise.resolve(),
				sendBackFeature: () => Promise.resolve(),
			}}
			now={() => NOW}
		/>,
	);
	return { actions: { merge, sendBack }, loads };
}

async function card(title: string) {
	return within(
		await within(screen.getByRole("tabpanel")).findByRole("article", {
			name: title,
		}),
	);
}

// jsdom has HTMLDialogElement but neither showModal nor close, which stand in
// for the browser's here, as in design/ui's own dialog test.
beforeEach(() => {
	HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement) {
		this.open = true;
	};
	HTMLDialogElement.prototype.close = function (this: HTMLDialogElement) {
		if (!this.open) return;
		this.open = false;
		this.dispatchEvent(new Event("close"));
	};
});

afterAll(() => {
	Reflect.deleteProperty(HTMLDialogElement.prototype, "showModal");
	Reflect.deleteProperty(HTMLDialogElement.prototype, "close");
});

afterEach(cleanup);

/** Whether the button can be pressed. */
function enabled(element: HTMLElement): boolean {
	return !(element as HTMLButtonElement).disabled;
}

/** The item's Merge button, once the link's state has arrived. */
async function mergeButton(title: string) {
	const found = await card(title);
	return found.getByRole("button", { name: "Merge" });
}

describe("merging", () => {
	test("Merge opens the confirmation and nothing is sent before it is confirmed", async () => {
		const { actions, loads } = open([
			item({
				title: "The vault",
				decisions: [
					{ chosen: "AES-GCM.", alternative: null, why: null },
					{ chosen: "In memory.", alternative: null, why: null },
				],
			}),
		]);
		const button = await mergeButton("The vault");
		await waitFor(() => expect(enabled(button)).toBe(true));

		fireEvent.click(button);

		const dialog = within(
			await screen.findByRole("dialog", { name: "Merge #304 into main?" }),
		);
		expect(dialog.getByText("The vault")).toBeTruthy();
		expect(dialog.getByText("4bf2a9c").tagName).toBe("CODE");
		expect(
			dialog.getByText("This confirms 2 decisions the agent made."),
		).toBeTruthy();
		expect(actions.merge).not.toHaveBeenCalled();

		// Cancelling sends nothing.
		fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(actions.merge).not.toHaveBeenCalled();

		// Confirming sends the commit the page showed.
		fireEvent.click(button);
		const again = within(await screen.findByRole("dialog"));
		const reads = loads.mock.calls.length;
		fireEvent.click(again.getByRole("button", { name: "Merge" }));

		await waitFor(() =>
			expect(actions.merge).toHaveBeenCalledWith(item().id, HEAD),
		);
		expect(await screen.findByText("Merged #304")).toBeTruthy();
		await waitFor(() => expect(loads.mock.calls.length).toBeGreaterThan(reads));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
	});

	test("without decisions the confirmation says nothing of them", async () => {
		open([item({ title: "The poll" })]);
		const button = await mergeButton("The poll");
		await waitFor(() => expect(enabled(button)).toBe(true));

		fireEvent.click(button);

		const dialog = within(await screen.findByRole("dialog"));
		expect(dialog.queryByText(/decision/)).toBeNull();
	});

	test("a waiting item that cannot merge has Merge disabled with the reason", async () => {
		open([item({ title: "The poll", can_merge: false })]);

		const found = await card("The poll");
		const button = found.getByRole("button", { name: "Merge" });
		await found.findByText("The checks are not green on this commit yet");
		expect(enabled(button)).toBe(false);
		expect(button.getAttribute("aria-describedby")).toBe(
			found.getByText("The checks are not green on this commit yet").id,
		);
	});

	test("without a usable link Merge and Send back are disabled, saying why", async () => {
		open([item({ title: "The poll" })], { link: "needs_relink" });

		const found = await card("The poll");
		await found.findByText(
			"Your GitHub link has expired. Link it again on the Projects tab to merge or comment.",
		);
		expect(enabled(found.getByRole("button", { name: "Merge" }))).toBe(false);
		expect(enabled(found.getByRole("button", { name: "Send back" }))).toBe(
			false,
		);
		cleanup();

		open([item({ title: "The poll" })], { link: "none" });

		const unlinked = await card("The poll");
		await unlinked.findByText(
			"Link GitHub on the Projects tab to merge or comment as yourself.",
		);
		expect(enabled(unlinked.getByRole("button", { name: "Merge" }))).toBe(
			false,
		);
	});

	test("only the first waiting item's Merge is the primary button", async () => {
		open([item({ number: 304 }), item({ number: 305 })]);

		const first = await mergeButton("Pull request 304");
		const second = await mergeButton("Pull request 305");
		await waitFor(() => expect(enabled(first)).toBe(true));
		expect(first.className).not.toBe(second.className);
		expect(
			screen
				.getAllByRole("button", { name: "Send back" })
				.every((each) => each.className === second.className),
		).toBe(true);
	});

	test("a refusal is shown in the item, and the list is read again", async () => {
		const { loads } = open([item({ title: "The vault" })], {
			actions: {
				merge: () =>
					Promise.reject(
						new ApiError({
							status: 409,
							title: "The pull request has changed since this was shown",
							detail: "It was read again. Look at it before you merge.",
						}),
					),
			},
		});
		const button = await mergeButton("The vault");
		await waitFor(() => expect(enabled(button)).toBe(true));
		fireEvent.click(button);
		const reads = loads.mock.calls.length;
		fireEvent.click(
			within(await screen.findByRole("dialog")).getByRole("button", {
				name: "Merge",
			}),
		);

		const found = await card("The vault");
		const alert = await found.findByRole("alert");
		expect(alert.textContent).toBe(
			"The pull request has changed since this was shown. It was read again. Look at it before you merge.",
		);
		await waitFor(() => expect(loads.mock.calls.length).toBeGreaterThan(reads));
		expect(screen.queryByText(/Merged #/)).toBeNull();
	});
});

describe("sending back", () => {
	test("Send back is disabled until there is text", async () => {
		const { actions } = open([item({ title: "The vault" })]);
		const found = await card("The vault");
		const sendBack = found.getByRole("button", { name: "Send back" });
		await waitFor(() => expect(enabled(sendBack)).toBe(true));

		fireEvent.click(sendBack);

		const box = found.getByRole("textbox", { name: "Comment" });
		const send = found.getByRole("button", { name: "Send" });
		expect(enabled(send)).toBe(false);
		fireEvent.change(box, { target: { value: "   " } });
		expect(enabled(send)).toBe(false);
		fireEvent.change(box, { target: { value: "Rename it.\n\nThen merge." } });
		expect(enabled(send)).toBe(true);
		expect(actions.sendBack).not.toHaveBeenCalled();

		fireEvent.click(send);

		await waitFor(() =>
			expect(actions.sendBack).toHaveBeenCalledWith(
				item().id,
				"Rename it.\n\nThen merge.",
			),
		);
		expect(await screen.findByText("Sent back #304")).toBeTruthy();
	});

	test("Cancel closes the box and sends nothing", async () => {
		const { actions } = open([item({ title: "The vault" })]);
		const found = await card("The vault");
		const sendBack = found.getByRole("button", { name: "Send back" });
		await waitFor(() => expect(enabled(sendBack)).toBe(true));
		fireEvent.click(sendBack);
		fireEvent.change(found.getByRole("textbox", { name: "Comment" }), {
			target: { value: "Never mind." },
		});

		fireEvent.click(found.getByRole("button", { name: "Cancel" }));

		expect(found.queryByRole("textbox")).toBeNull();
		expect(actions.sendBack).not.toHaveBeenCalled();
	});

	test("a stopped item has no Merge and its button reads Comment to run again", async () => {
		const { actions } = open([
			item({ title: "Import a photo", kind: "stopped", can_merge: false }),
		]);
		const found = await card("Import a photo");

		expect(found.queryByRole("button", { name: "Merge" })).toBeNull();
		expect(found.queryByRole("button", { name: "Send back" })).toBeNull();
		const comment = found.getByRole("button", { name: "Comment to run again" });
		await waitFor(() => expect(enabled(comment)).toBe(true));
		fireEvent.click(comment);
		fireEvent.change(
			found.getByRole("textbox", { name: "A comment runs it again." }),
			{ target: { value: "Try once more." } },
		);
		fireEvent.click(found.getByRole("button", { name: "Send" }));

		await waitFor(() =>
			expect(actions.sendBack).toHaveBeenCalledWith(
				item().id,
				"Try once more.",
			),
		);
	});

	test("an item sent back has no action but Open on GitHub", async () => {
		open([item({ title: "The board", sent_back: true })]);
		const found = await card("The board");

		expect(found.queryAllByRole("button")).toHaveLength(0);
		expect(found.getByRole("link", { name: "Open on GitHub" })).toBeTruthy();
	});
});

describe("the nav's dot", () => {
	/** How many times the page has asked the nav bar to ask again, from now. */
	function asks() {
		const heard = vi.fn();
		window.addEventListener("platform-nav-attention", heard);
		return {
			heard,
			stop: () => window.removeEventListener("platform-nav-attention", heard),
		};
	}

	test("the nav is asked again after a merge", async () => {
		const { heard, stop } = asks();
		open([item({ title: "The vault" })]);
		const button = await mergeButton("The vault");
		await waitFor(() => expect(enabled(button)).toBe(true));
		expect(heard).not.toHaveBeenCalled();

		fireEvent.click(button);
		fireEvent.click(
			within(await screen.findByRole("dialog")).getByRole("button", {
				name: "Merge",
			}),
		);

		await waitFor(() => expect(heard).toHaveBeenCalledTimes(1));
		stop();
	});

	test("the nav is asked again after a merge is refused", async () => {
		const { heard, stop } = asks();
		open([item({ title: "The vault" })], {
			actions: {
				merge: () =>
					Promise.reject(
						new ApiError({
							status: 409,
							title: "Your GitHub link has expired",
							detail: null,
						}),
					),
			},
		});
		const button = await mergeButton("The vault");
		await waitFor(() => expect(enabled(button)).toBe(true));

		fireEvent.click(button);
		fireEvent.click(
			within(await screen.findByRole("dialog")).getByRole("button", {
				name: "Merge",
			}),
		);

		// What a refusal stores, such as an expired link, can change the dot too.
		await waitFor(() => expect(heard).toHaveBeenCalledTimes(1));
		stop();
	});

	test("the nav is asked again after sending back", async () => {
		const { heard, stop } = asks();
		open([item({ title: "The vault" })]);
		const found = await card("The vault");
		const sendBack = found.getByRole("button", { name: "Send back" });
		await waitFor(() => expect(enabled(sendBack)).toBe(true));

		fireEvent.click(sendBack);
		fireEvent.change(found.getByRole("textbox", { name: "Comment" }), {
			target: { value: "Rename it." },
		});
		fireEvent.click(found.getByRole("button", { name: "Send" }));

		await waitFor(() => expect(heard).toHaveBeenCalledTimes(1));
		stop();
	});
});
