import {
	Children,
	type ComponentProps,
	createContext,
	isValidElement,
	type KeyboardEvent,
	type ReactNode,
	useContext,
	useId,
} from "react";
import { Badge } from "../status/badge";
import styles from "./tabs.module.css";

type TabsState = {
	/** The selected value, as the caller gave it. */
	value: string;
	/** The tab in the tab order: the selected one, or the first when the
	 * value names none, so the row can always be reached. */
	focusable: string | undefined;
	select: (value: string) => void;
	tabId: (value: string) => string;
	panelId: (value: string) => string;
};

const TabsContext = createContext<TabsState | null>(null);

function useTabs(component: string): TabsState {
	const state = useContext(TabsContext);
	if (!state) throw new Error(`${component} is used outside Tabs`);
	return state;
}

export type TabsProps = Omit<ComponentProps<"div">, "onChange" | "children"> & {
	/** The tablist's accessible name, since a page can hold more than one. */
	label: string;
	/** The selected tab's value. The caller owns it, so a page can keep it in
	 * the address. */
	value: string;
	/** Called with a tab's value when the user picks it, by a click or by the
	 * keyboard. The selection changes only when the caller passes it back. */
	onChange: (value: string) => void;
	/** The `Tab`s, which go in the row in their order, and the `TabPanel`s,
	 * which follow it. Each Tab and TabPanel are direct children. */
	children: ReactNode;
};

/** "Tabs" in section 4 of design/DESIGN.md, with the keyboard pattern for
 * tabs: the selected tab alone is in the tab order, Left and Right move and
 * wrap, Home and End go to the ends, and activation follows focus. */
export function Tabs({
	label,
	value,
	onChange,
	className,
	children,
	...rest
}: TabsProps) {
	const base = useId();
	const items = Children.toArray(children);
	const panels = items.filter(
		(child) => isValidElement(child) && child.type === TabPanel,
	);
	const row = items.filter((child) => !panels.includes(child));
	const values = row.flatMap((child) =>
		isValidElement<TabProps>(child) && child.type === Tab
			? [child.props.value]
			: [],
	);
	// An id may hold no whitespace, and a value may come from the address.
	const id = (kind: string, v: string) =>
		`${base}-${kind}-${encodeURIComponent(v)}`;

	const state: TabsState = {
		value,
		focusable: values.includes(value) ? value : values[0],
		select: (next) => {
			if (next !== value) onChange(next);
		},
		tabId: (v) => id("tab", v),
		panelId: (v) => id("panel", v),
	};

	function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
		const focused = event.target instanceof HTMLElement ? event.target.id : "";
		const current = values.findIndex((v) => state.tabId(v) === focused);
		if (current < 0) return;
		const last = values.length - 1;
		const target = {
			ArrowRight: current === last ? 0 : current + 1,
			ArrowLeft: current === 0 ? last : current - 1,
			Home: 0,
			End: last,
		}[event.key];
		const next = target === undefined ? undefined : values[target];
		if (next === undefined) return;
		event.preventDefault();
		document.getElementById(state.tabId(next))?.focus();
		state.select(next);
	}

	return (
		<TabsContext.Provider value={state}>
			<div
				className={[styles.tabs, className].filter(Boolean).join(" ")}
				{...rest}
			>
				<div
					role="tablist"
					aria-label={label}
					className={styles.row}
					onKeyDown={onKeyDown}
				>
					{row}
				</div>
				{panels}
			</div>
		</TabsContext.Provider>
	);
}

export type TabProps = Omit<
	ComponentProps<"button">,
	"value" | "id" | "role" | "type" | "tabIndex" | "children"
> & {
	/** What `Tabs` reports and selects by, unique among its tabs. */
	value: string;
	/** The label. */
	children: ReactNode;
	/** A small count after the label, shown as a `Badge`. */
	count?: number;
};

/** One tab: a real button with `role="tab"`, inside `Tabs`. */
export function Tab({
	value,
	count,
	className,
	onClick,
	children,
	...rest
}: TabProps) {
	const tabs = useTabs("Tab");
	const selected = tabs.value === value;
	return (
		<button
			{...rest}
			type="button"
			role="tab"
			id={tabs.tabId(value)}
			aria-selected={selected}
			aria-controls={tabs.panelId(value)}
			tabIndex={tabs.focusable === value ? 0 : -1}
			className={[styles.tab, className].filter(Boolean).join(" ")}
			onClick={(event) => {
				onClick?.(event);
				if (!event.defaultPrevented) tabs.select(value);
			}}
		>
			{children}
			{/* The space keeps the label and the count apart in the tab's name;
			 * a flex row renders no whitespace, so it does not move the badge. */}
			{count === undefined ? null : (
				<>
					{" "}
					<Badge>{count}</Badge>
				</>
			)}
		</button>
	);
}

export type TabPanelProps = Omit<
	ComponentProps<"div">,
	"id" | "role" | "hidden"
> & {
	/** The value of the tab that labels it and shows it. */
	value: string;
};

/** The panel of the tab with the same value, labelled by it, and hidden
 * while another tab is selected. It is in the tab order, as the pattern asks,
 * so a panel with no control in it can still be reached. */
export function TabPanel({ value, ...rest }: TabPanelProps) {
	const tabs = useTabs("TabPanel");
	return (
		<div
			// biome-ignore lint/a11y/noNoninteractiveTabindex: the tabs pattern puts a panel in the tab order, so one without a control can be reached.
			tabIndex={0}
			{...rest}
			role="tabpanel"
			id={tabs.panelId(value)}
			aria-labelledby={tabs.tabId(value)}
			hidden={tabs.value !== value}
		/>
	);
}
