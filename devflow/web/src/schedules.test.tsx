import {
	cleanup,
	fireEvent,
	render,
	screen,
	waitFor,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test, vi } from "vitest";
import { ApiError } from "./api/client";
import {
	daysLine,
	HELP,
	type ProjectSchedules,
	SAVED,
	type SchedulesApi,
	SchedulesCard,
	type SchedulesRequest,
} from "./schedules";

afterEach(cleanup);

const checked = (element: HTMLElement) => (element as HTMLInputElement).checked;
const valueIn = (element: HTMLElement) =>
	(element as HTMLInputElement | HTMLSelectElement).value;

const ID = "0190a8f0-0000-7000-8000-000000000001";

/** Dev flow's two routes, in memory: the list replaced whole, a new window
 * given an id, and a name given twice refused as Dev flow refuses it. */
function fakeApi(start: Partial<ProjectSchedules> = {}) {
	let state: ProjectSchedules = {
		runs_by: "always",
		time_zone: "Europe/Stockholm",
		schedules: [],
		next_start: null,
		running_until: null,
		...start,
	};
	let ids = 0;
	const saveSchedules = vi.fn(
		async (_id: string, request: SchedulesRequest) => {
			const names = request.schedules.map((each) => each.name.toLowerCase());
			const twice = names.findIndex(
				(name, index) => names.indexOf(name) < index,
			);
			if (twice >= 0)
				throw new ApiError({
					status: 422,
					title: "The request breaks a rule",
					detail: null,
					errors: {
						[`schedules[${twice}].name`]: [
							"is the name of another window, whatever its case",
						],
					},
				});
			state = {
				...state,
				runs_by: request.runs_by,
				schedules: request.schedules.map((each) => ({
					...each,
					id: each.id ?? `0190a8f0-0000-7000-8000-00000000010${ids++}`,
				})),
			};
			return state;
		},
	);
	const saveTimeZone = vi.fn(async (zone: string) => {
		if (!zone.includes("/") && zone !== "UTC")
			throw new ApiError({
				status: 422,
				title: "The request breaks a rule",
				detail: null,
				errors: { time_zone: ["must be an IANA time zone"] },
			});
		state = { ...state, time_zone: zone };
		return zone;
	});
	const api: SchedulesApi = {
		loadSchedules: vi.fn(async () => state),
		saveSchedules,
		saveTimeZone,
	};
	return { api, saveSchedules, saveTimeZone, current: () => state };
}

const NIGHT = {
	id: "0190a8f0-0000-7000-8000-000000000020",
	name: "Night",
	days: ["mon", "tue", "wed", "thu", "fri"],
	from: "22:00",
	to: "06:00",
	active: true,
};

async function card(api: SchedulesApi) {
	render(<SchedulesCard id={ID} anchor="settings-schedules" {...api} />);
	return screen.findByRole("article", { name: "When it runs" });
}

describe("the card When it runs", () => {
	test("windows are added, switched, edited and removed, and the mode chosen", async () => {
		const { api, saveSchedules, current } = fakeApi();
		const region = await card(api);

		// Always, at first, with what holds outside a schedule said.
		const always = within(region).getByRole("radio", { name: "Always" });
		const bySchedule = within(region).getByRole("radio", {
			name: "By schedule",
		});
		expect(checked(always)).toBe(true);
		expect(within(region).getByText(HELP)).toBeTruthy();
		expect(
			within(region).queryByRole("button", { name: "Add a schedule" }),
		).toBeNull();

		// By schedule is chosen as it is clicked, and saved at once, with no
		// windows.
		fireEvent.click(bySchedule);
		expect(checked(bySchedule)).toBe(true);
		await waitFor(() =>
			expect(saveSchedules).toHaveBeenLastCalledWith(ID, {
				runs_by: "schedule",
				schedules: [],
			}),
		);
		expect(await within(region).findByText(SAVED)).toBeTruthy();
		expect(checked(bySchedule)).toBe(true);

		// A window added: its name, Monday to Friday and its two times.
		fireEvent.click(
			within(region).getByRole("button", { name: "Add a schedule" }),
		);
		const editor = within(region).getByRole("form", {
			name: "A new schedule",
		});
		fireEvent.change(within(editor).getByLabelText("Name"), {
			target: { value: "Night" },
		});
		for (const day of ["Mon", "Tue", "Wed", "Thu", "Fri"])
			fireEvent.click(within(editor).getByRole("checkbox", { name: day }));
		fireEvent.change(within(editor).getByLabelText("From"), {
			target: { value: "22:00" },
		});
		fireEvent.change(within(editor).getByLabelText("To"), {
			target: { value: "06:00" },
		});
		fireEvent.click(within(editor).getByRole("button", { name: "Add" }));
		await waitFor(() =>
			expect(saveSchedules).toHaveBeenLastCalledWith(ID, {
				runs_by: "schedule",
				schedules: [
					{
						id: null,
						name: "Night",
						days: ["mon", "tue", "wed", "thu", "fri"],
						from: "22:00",
						to: "06:00",
						active: true,
					},
				],
			}),
		);
		// Its row: the name its switch's, the days and the times its line.
		const night = await within(region).findByRole("switch", { name: "Night" });
		expect(checked(night)).toBe(true);
		expect(within(region).getByText("Mon–Fri · 22:00–06:00")).toBeTruthy();
		expect(
			within(region).queryByRole("form", { name: "A new schedule" }),
		).toBeNull();

		// A second, and its name given twice is refused under its field.
		fireEvent.click(
			within(region).getByRole("button", { name: "Add a schedule" }),
		);
		const second = within(region).getByRole("form", {
			name: "A new schedule",
		});
		fireEvent.change(within(second).getByLabelText("Name"), {
			target: { value: "night" },
		});
		fireEvent.click(within(second).getByRole("checkbox", { name: "Sat" }));
		fireEvent.change(within(second).getByLabelText("From"), {
			target: { value: "10:00" },
		});
		fireEvent.change(within(second).getByLabelText("To"), {
			target: { value: "14:00" },
		});
		fireEvent.click(within(second).getByRole("button", { name: "Add" }));
		expect(
			await within(second).findByText(
				/is the name of another window, whatever its case/,
			),
		).toBeTruthy();
		fireEvent.change(within(second).getByLabelText("Name"), {
			target: { value: "Weekend" },
		});
		fireEvent.click(within(second).getByRole("button", { name: "Add" }));
		const weekend = await within(region).findByRole("switch", {
			name: "Weekend",
		});

		// Switched off, at once, the other left as it was.
		fireEvent.click(weekend);
		expect(checked(weekend)).toBe(false);
		await waitFor(() =>
			expect(current().schedules.map((each) => each.active)).toEqual([
				true,
				false,
			]),
		);
		expect(
			checked(within(region).getByRole("switch", { name: "Weekend" })),
		).toBe(false);

		// Edited: Night moves to start at 23:00, keeping its id.
		const nightId = current().schedules[0]?.id;
		fireEvent.click(within(region).getByRole("button", { name: "Edit Night" }));
		const editing = within(region).getByRole("form", { name: "Edit Night" });
		expect(valueIn(within(editing).getByLabelText("Name"))).toBe("Night");
		expect(
			checked(within(editing).getByRole("checkbox", { name: "Fri" })),
		).toBe(true);
		expect(
			checked(within(editing).getByRole("checkbox", { name: "Sat" })),
		).toBe(false);
		fireEvent.change(within(editing).getByLabelText("From"), {
			target: { value: "23:00" },
		});
		fireEvent.click(within(editing).getByRole("button", { name: "Save" }));
		await waitFor(() =>
			expect(current().schedules[0]).toMatchObject({
				id: nightId,
				name: "Night",
				from: "23:00",
			}),
		);
		expect(
			await within(region).findByText("Mon–Fri · 23:00–06:00"),
		).toBeTruthy();

		// Removed, Weekend alone is left.
		fireEvent.click(
			within(region).getByRole("button", { name: "Remove Night" }),
		);
		await waitFor(() =>
			expect(current().schedules.map((each) => each.name)).toEqual(["Weekend"]),
		);
		expect(within(region).queryByRole("switch", { name: "Night" })).toBeNull();

		// Back to always, the windows kept and hidden.
		fireEvent.click(always);
		await waitFor(() => expect(current().runs_by).toBe("always"));
		expect(current().schedules).toHaveLength(1);
		expect(
			within(region).queryByRole("switch", { name: "Weekend" }),
		).toBeNull();
	});

	test("a change Dev flow does not take goes back", async () => {
		const { api } = fakeApi();
		api.saveSchedules = vi.fn(() => Promise.reject(new Error("down")));
		const region = await card(api);
		const bySchedule = within(region).getByRole("radio", {
			name: "By schedule",
		});

		fireEvent.click(bySchedule);
		expect(checked(bySchedule)).toBe(true);
		expect((await within(region).findByRole("alert")).textContent).toMatch(
			/^The schedules were not saved: /,
		);
		expect(checked(bySchedule)).toBe(false);
		expect(checked(within(region).getByRole("radio", { name: "Always" }))).toBe(
			true,
		);
	});

	test("the time zone is changed at once, for every project", async () => {
		const { api, saveTimeZone } = fakeApi({
			runs_by: "schedule",
			schedules: [NIGHT],
		});
		const region = await card(api);
		expect(
			within(region).getByText("Times are in Europe/Stockholm"),
		).toBeTruthy();

		fireEvent.click(within(region).getByRole("button", { name: "Change" }));
		const zone = within(region).getByLabelText("Time zone");
		expect(valueIn(zone)).toBe("Europe/Stockholm");
		expect(within(region).getByText("Used by every project")).toBeTruthy();
		fireEvent.change(zone, { target: { value: "America/New_York" } });
		await waitFor(() =>
			expect(saveTimeZone).toHaveBeenCalledWith("America/New_York"),
		);
		expect(
			await within(region).findByText("Times are in America/New_York"),
		).toBeTruthy();
		expect(within(region).getByText(SAVED)).toBeTruthy();
	});

	test("a window's days are said in short", () => {
		expect(daysLine(["mon", "tue", "wed", "thu", "fri"])).toBe("Mon–Fri");
		expect(daysLine(["mon", "tue", "wed", "thu", "fri", "sat", "sun"])).toBe(
			"Every day",
		);
		expect(daysLine(["sat", "sun"])).toBe("Sat, Sun");
		expect(daysLine(["mon", "wed", "thu", "fri", "sun"])).toBe(
			"Mon, Wed–Fri, Sun",
		);
	});
});
