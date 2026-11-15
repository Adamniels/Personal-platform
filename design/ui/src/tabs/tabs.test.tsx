import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Tab, TabPanel, Tabs } from "../index";

afterEach(cleanup);

const names = ["Agents", "Decisions", "Logs", "History"];

/** Tabs whose selection lives in the harness, as a page keeps it. */
function Harness({
	initial = "agents",
	onChange,
}: {
	initial?: string;
	onChange?: (value: string) => void;
}) {
	const [value, setValue] = useState(initial);
	return (
		<Tabs
			label="Sections"
			value={value}
			onChange={(next) => {
				setValue(next);
				onChange?.(next);
			}}
		>
			{names.map((name) => (
				<Tab key={name} value={name.toLowerCase()}>
					{name}
				</Tab>
			))}
			{names.map((name) => (
				<TabPanel key={name} value={name.toLowerCase()}>
					The {name} panel
				</TabPanel>
			))}
		</Tabs>
	);
}

function tab(name: string) {
	return screen.getByRole("tab", { name });
}

function press(key: string) {
	const focused = document.activeElement;
	if (!focused) throw new Error("nothing is focused");
	fireEvent.keyDown(focused, { key });
}

describe("tabs follow the keyboard pattern for tabs", () => {
	it("is a tablist of tabs, named by its label", () => {
		render(<Harness />);

		const list = screen.getByRole("tablist", { name: "Sections" });
		expect(list).toBeDefined();
		const tabs = screen.getAllByRole("tab");
		expect(tabs.map((t) => t.textContent)).toEqual(names);
		for (const t of tabs) {
			expect(t.tagName).toBe("BUTTON");
			expect(t.getAttribute("type")).toBe("button");
			expect(list.contains(t)).toBe(true);
		}
	});

	it("marks the selected tab alone, and puts it alone in the tab order", () => {
		render(<Harness initial="logs" />);

		for (const name of names) {
			const selected = name === "Logs";
			expect(tab(name).getAttribute("aria-selected")).toBe(String(selected));
			expect(tab(name).tabIndex).toBe(selected ? 0 : -1);
		}
	});

	it("moves with Right and Left, and wraps at both ends", () => {
		render(<Harness />);
		tab("Agents").focus();

		press("ArrowRight");
		expect(document.activeElement).toBe(tab("Decisions"));
		press("ArrowLeft");
		press("ArrowLeft");
		expect(document.activeElement).toBe(tab("History"));
		press("ArrowRight");
		expect(document.activeElement).toBe(tab("Agents"));
	});

	it("goes to the first with Home and the last with End", () => {
		render(<Harness initial="decisions" />);
		tab("Decisions").focus();

		press("End");
		expect(document.activeElement).toBe(tab("History"));
		press("Home");
		expect(document.activeElement).toBe(tab("Agents"));
	});

	it("selects the tab focus moves to, since activation follows focus", () => {
		render(<Harness />);
		tab("Agents").focus();

		press("ArrowRight");
		expect(tab("Decisions").getAttribute("aria-selected")).toBe("true");
		expect(tab("Decisions").tabIndex).toBe(0);
		expect(tab("Agents").tabIndex).toBe(-1);
	});

	it("ignores other keys", () => {
		const onChange = vi.fn();
		render(<Harness onChange={onChange} />);
		tab("Agents").focus();

		press("ArrowDown");
		press("a");
		expect(document.activeElement).toBe(tab("Agents"));
		expect(onChange).not.toHaveBeenCalled();
	});

	it("labels each panel by its tab, and shows only the selected one", () => {
		render(<Harness initial="logs" />);

		for (const name of names) {
			const panel = screen.getByText(`The ${name} panel`);
			expect(panel.getAttribute("role")).toBe("tabpanel");
			expect(panel.getAttribute("aria-labelledby")).toBe(tab(name).id);
			expect(tab(name).getAttribute("aria-controls")).toBe(panel.id);
			expect(panel.hidden).toBe(name !== "Logs");
		}
		expect(screen.getByRole("tabpanel", { name: "Logs" })).toBeDefined();
	});

	it("keeps ids valid when a value holds a space", () => {
		render(
			<Tabs label="Spaced" value="two words" onChange={() => {}}>
				<Tab value="two words">Two words</Tab>
				<TabPanel value="two words">Panel</TabPanel>
			</Tabs>,
		);

		expect(tab("Two words").id).not.toMatch(/\s/);
		expect(screen.getByRole("tabpanel").id).not.toMatch(/\s/);
	});

	it("keeps the first tab reachable when the value matches none", () => {
		render(
			<Tabs label="Stale" value="gone" onChange={() => {}}>
				<Tab value="one">One</Tab>
				<Tab value="two">Two</Tab>
			</Tabs>,
		);

		expect(tab("One").tabIndex).toBe(0);
		expect(tab("One").getAttribute("aria-selected")).toBe("false");
		expect(tab("Two").tabIndex).toBe(-1);
	});

	it("shows a count after the label, as part of the tab's name", () => {
		render(
			<Tabs label="Counted" value="one" onChange={() => {}}>
				<Tab value="one">One</Tab>
				<Tab value="decisions" count={2}>
					Decisions
				</Tab>
			</Tabs>,
		);

		const counted = screen.getByRole("tab", { name: "Decisions 2" });
		expect(counted.lastElementChild?.textContent).toBe("2");
	});
});

describe("tabs can be controlled by the caller", () => {
	it("selects the tab its value names", () => {
		const { rerender } = render(
			<Tabs label="Controlled" value="b" onChange={() => {}}>
				<Tab value="a">A</Tab>
				<Tab value="b">B</Tab>
			</Tabs>,
		);
		expect(tab("B").getAttribute("aria-selected")).toBe("true");

		rerender(
			<Tabs label="Controlled" value="a" onChange={() => {}}>
				<Tab value="a">A</Tab>
				<Tab value="b">B</Tab>
			</Tabs>,
		);
		expect(tab("A").getAttribute("aria-selected")).toBe("true");
		expect(tab("B").getAttribute("aria-selected")).toBe("false");
	});

	it("reports a click and a key as a change, and does not select by itself", () => {
		const onChange = vi.fn();
		render(
			<Tabs label="Controlled" value="a" onChange={onChange}>
				<Tab value="a">A</Tab>
				<Tab value="b">B</Tab>
			</Tabs>,
		);

		fireEvent.click(tab("B"));
		expect(onChange).toHaveBeenLastCalledWith("b");
		expect(tab("A").getAttribute("aria-selected")).toBe("true");

		tab("A").focus();
		press("End");
		expect(onChange).toHaveBeenLastCalledWith("b");
		expect(onChange).toHaveBeenCalledTimes(2);
	});

	it("does not report a click on the tab already selected", () => {
		const onChange = vi.fn();
		render(
			<Tabs label="Controlled" value="a" onChange={onChange}>
				<Tab value="a">A</Tab>
				<Tab value="b">B</Tab>
			</Tabs>,
		);

		fireEvent.click(tab("A"));
		expect(onChange).not.toHaveBeenCalled();
	});
});

/** Six tabs, one with a count; `count` keeps the first that many. */
const six = [
	{ value: "agents", label: "Agents" },
	{ value: "decisions", label: "Decisions", count: 2 },
	{ value: "logs", label: "Logs" },
	{ value: "history", label: "History" },
	{ value: "notes", label: "Notes" },
	{ value: "lessons", label: "Lessons" },
];

function Many({
	value,
	onChange,
	count = six.length,
}: {
	value: string;
	onChange: (value: string) => void;
	count?: number;
}) {
	const tabs = six.slice(0, count);
	return (
		<Tabs label="Sections" value={value} onChange={onChange}>
			{tabs.map((t) => (
				<Tab key={t.value} value={t.value} count={t.count}>
					{t.label}
				</Tab>
			))}
			{tabs.map((t) => (
				<TabPanel key={t.value} value={t.value}>
					The {t.label} panel
				</TabPanel>
			))}
		</Tabs>
	);
}

function picker() {
	return screen.getByRole<HTMLSelectElement>("combobox", {
		name: "Sections",
	});
}

describe("more than five tabs are also a select, which CSS shows on phone", () => {
	it("renders a select with every tab when there are more than five", () => {
		render(<Many value="decisions" onChange={() => {}} />);

		const options = Array.from(picker().options);
		expect(options.map((o) => o.textContent)).toEqual([
			"Agents",
			"Decisions (2)",
			"Logs",
			"History",
			"Notes",
			"Lessons",
		]);
		expect(options.map((o) => o.value)).toEqual(six.map((t) => t.value));
		expect(options.filter((o) => o.selected).map((o) => o.value)).toEqual([
			"decisions",
		]);
		expect(picker().value).toBe("decisions");
		// The tab list is still there; CSS decides which of the two shows.
		expect(screen.getByRole("tablist", { name: "Sections" })).toBeDefined();
	});

	it("choosing an option reports the tab's value", () => {
		const onChange = vi.fn();
		render(<Many value="agents" onChange={onChange} />);

		fireEvent.change(picker(), { target: { value: "lessons" } });

		expect(onChange).toHaveBeenCalledTimes(1);
		expect(onChange).toHaveBeenLastCalledWith("lessons");
		// Nothing changes until the caller passes the value back.
		expect(picker().value).toBe("agents");
		expect(tab("Agents").getAttribute("aria-selected")).toBe("true");
		expect(screen.getByText("The Lessons panel").hidden).toBe(true);
	});

	it("renders no select for five tabs or fewer", () => {
		render(<Many value="agents" onChange={() => {}} count={5} />);

		expect(screen.queryByRole("combobox")).toBeNull();
		expect(screen.getAllByRole("tab")).toHaveLength(5);
	});
});
