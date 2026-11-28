import {
	act,
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
import { App } from "./app";
import { fakeEvents } from "./events.testing";
import {
	type Machine,
	type MachineEnrollment,
	type MachinesApi,
	NO_MACHINE,
	NOT_READ,
	RUN_ONE,
} from "./machines";

// jsdom has HTMLDialogElement but neither showModal nor close; these stand in
// for the browser's, as design/ui's own dialog tests do.
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

const MINI = "0190a8f0-0000-7000-8000-0000000000a1";
const BOX = "0190a8f0-0000-7000-8000-0000000000b2";
const CODE = "dfe_AAAAAAAAAAAAAAAAAAAAAA";

function machines(): Machine[] {
	return [
		{
			id: MINI,
			name: "mac-mini",
			os: "macos",
			arch: "arm64",
			program_version: "4f2a9c1b7e30",
			docker: "running",
			online: true,
			last_seen_at: "2026-11-28T12:00:00+00:00",
		},
		{
			id: BOX,
			name: "build-box",
			os: "linux",
			arch: "x64",
			program_version: "4f2a9c1b7e20",
			docker: "Cannot connect to the Docker daemon",
			online: false,
			last_seen_at: "2026-11-27T09:30:00+00:00",
		},
	];
}

function enrollment(): MachineEnrollment {
	return {
		code: CODE,
		expires_at: "2026-11-28T13:00:00+00:00",
		command: `curl -fsSL 'https://platform.test/devflow/machine/install.sh' | sh -s -- 'https://platform.test' '${CODE}'`,
		local_command: `curl -fsSL 'http://127.0.0.1:9080/devflow/machine/install.sh' | sh -s -- 'http://127.0.0.1:9080' '${CODE}'`,
	};
}

function api(overrides: Partial<MachinesApi> = {}): MachinesApi {
	return {
		loadMachines: () => Promise.resolve(machines()),
		newEnrollment: () => Promise.resolve(enrollment()),
		removeMachine: () => Promise.resolve(),
		...overrides,
	};
}

function open(path: string, machinesApi: MachinesApi) {
	window.history.pushState(null, "", path);
	render(
		<App
			loadStatus={() => new Promise(() => {})}
			waiting={{
				loadWaiting: () => new Promise(() => {}),
				refresh: () => Promise.resolve(),
			}}
			running={{
				loadRunning: () => new Promise(() => {}),
				stopRun: () => Promise.resolve(),
				runNow: () => Promise.resolve(),
				openEvents: fakeEvents().open,
			}}
			pauses={{
				loadPaused: () => new Promise(() => {}),
				pauseAll: () => Promise.resolve(),
			}}
			machines={machinesApi}
		/>,
	);
}

afterEach(() => {
	cleanup();
	window.history.pushState(null, "", "/");
});

describe("Machines", () => {
	test("is the last tab, at its own address", async () => {
		open("/devflow/machines", api());
		const tabs = screen.getAllByRole("tab");
		expect(tabs.at(-1)?.textContent).toBe("Machines");
		expect(tabs.at(-1)?.getAttribute("aria-selected")).toBe("true");
		expect(await screen.findByRole("list", { name: "Machines" })).toBeDefined();
	});

	test("lists machines with online and version, and shows a new code once", async () => {
		const newEnrollment = vi.fn(() => Promise.resolve(enrollment()));
		open("/devflow/machines", api({ newEnrollment }));

		const list = await screen.findByRole("list", { name: "Machines" });
		const rows = within(list).getAllByRole("listitem");
		expect(rows).toHaveLength(2);
		// The Mac mini: online, with what it runs.
		const mini = rows[0] as HTMLElement;
		expect(within(mini).getByText("mac-mini")).toBeDefined();
		expect(within(mini).getByText("Online")).toBeDefined();
		expect(mini.textContent).toContain("macOS · arm64 · Program 4f2a9c1b7e30");
		expect(within(mini).getByText("Docker running")).toBeDefined();
		expect(within(mini).queryByText(/Not seen since/)).toBeNull();
		// The build box: offline since it was last seen, and why Docker is not
		// running, as something that needs the owner.
		const box = rows[1] as HTMLElement;
		expect(within(box).getByText("Offline")).toBeDefined();
		const since = within(box).getByText(/^Not seen since/);
		expect(since.querySelector("time")?.getAttribute("dateTime")).toBe(
			"2026-11-27T09:30:00+00:00",
		);
		expect(box.textContent).toContain("Linux · x64 · Program 4f2a9c1b7e20");
		expect(within(box).getByText("No Docker")).toBeDefined();
		expect(
			within(box).getByText("Cannot connect to the Docker daemon"),
		).toBeDefined();
		expect(
			screen.getByRole("article", { name: "Machines" }).textContent,
		).toContain("1 of 2 online");

		// Add a machine: both commands with the code, and when it expires.
		fireEvent.click(screen.getByRole("button", { name: "Add a machine" }));
		const dialog = await screen.findByRole("dialog", { name: "Add a machine" });
		expect(newEnrollment).toHaveBeenCalledOnce();
		expect(dialog.textContent).toContain(RUN_ONE);
		const any = within(dialog).getByRole("region", { name: "On any machine" });
		expect(any.textContent).toContain(enrollment().command);
		const local = within(dialog).getByRole("region", {
			name: "On the machine the platform runs on",
		});
		expect(local.textContent).toContain(enrollment().local_command);
		expect(
			within(dialog)
				.getByText(/^The code expires at/)
				.querySelector("time")
				?.getAttribute("dateTime"),
		).toBe("2026-11-28T13:00:00+00:00");

		// Closed, the code is gone, and only a new one shows another.
		fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
		await waitFor(() =>
			expect(
				screen.queryByRole("dialog", { name: "Add a machine" }),
			).toBeNull(),
		);
		expect(screen.queryByText(CODE, { exact: false })).toBeNull();
	});

	test("copies a command", async () => {
		const copy = vi.fn(() => Promise.resolve());
		window.history.pushState(null, "", "/devflow/machines");
		const { Machines } = await import("./machines");
		const { QueryClient, QueryClientProvider } = await import(
			"@tanstack/react-query"
		);
		render(
			<QueryClientProvider client={new QueryClient()}>
				<Machines {...api()} copy={copy} />
			</QueryClientProvider>,
		);
		fireEvent.click(
			await screen.findByRole("button", { name: "Add a machine" }),
		);
		const dialog = await screen.findByRole("dialog", { name: "Add a machine" });
		await act(async () => {
			fireEvent.click(
				within(dialog).getByRole("button", { name: "Copy the local command" }),
			);
		});
		expect(copy).toHaveBeenCalledWith(enrollment().local_command);
		expect(
			within(dialog).getByRole("button", { name: "Copied" }),
		).toBeDefined();
	});

	test("removes a machine after asking", async () => {
		const removeMachine = vi.fn(() => Promise.resolve());
		const loadMachines = vi.fn(() => Promise.resolve(machines()));
		open("/devflow/machines", api({ removeMachine, loadMachines }));
		const list = await screen.findByRole("list", { name: "Machines" });

		fireEvent.click(
			within(list).getByRole("button", { name: "Remove build-box" }),
		);
		const asking = await screen.findByRole("dialog", {
			name: "Remove build-box?",
		});
		expect(asking.textContent).toContain("Its key stops working at once.");
		expect(removeMachine).not.toHaveBeenCalled();
		await act(async () => {
			fireEvent.click(within(asking).getByRole("button", { name: "Remove" }));
		});

		expect(removeMachine).toHaveBeenCalledWith(BOX);
		// The list is read again.
		await waitFor(() =>
			expect(loadMachines.mock.calls.length).toBeGreaterThan(1),
		);
	});

	test("says when there is no machine, or the list cannot be read", async () => {
		open("/devflow/machines", api({ loadMachines: () => Promise.resolve([]) }));
		expect(await screen.findByText(NO_MACHINE)).toBeDefined();
		cleanup();

		open(
			"/devflow/machines",
			api({ loadMachines: () => Promise.reject(new Error("down")) }),
		);
		expect((await screen.findByRole("alert")).textContent).toBe(NOT_READ);
	});
});
