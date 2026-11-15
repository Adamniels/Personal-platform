import { useState } from "react";
import { Tab, TabPanel, Tabs } from "../../src";
import { Note, Row, Section } from "./section";

const four = [
	{ value: "agents", label: "Agents" },
	{ value: "decisions", label: "Decisions", count: 2 },
	{ value: "logs", label: "Logs" },
	{ value: "history", label: "History" },
];

const eight = [
	"Overview",
	"Sessions",
	"Notes",
	"Lessons",
	"Decisions",
	"Agents",
	"Logs",
	"History",
];

/** Four tabs, one with a count, and eight.
 * Each keeps its own selection, as a page would in its address. */
export function TabsSection() {
	const [first, setFirst] = useState("agents");
	const [second, setSecond] = useState("overview");
	return (
		<Section title="Tabs" wide>
			<Row label="Four tabs" stack>
				<Tabs label="Four tabs" value={first} onChange={setFirst}>
					{four.map((tab) => (
						<Tab key={tab.value} value={tab.value} count={tab.count}>
							{tab.label}
						</Tab>
					))}
					{four.map((tab) => (
						<TabPanel key={tab.value} value={tab.value}>
							The {tab.label.toLowerCase()} panel.
						</TabPanel>
					))}
				</Tabs>
			</Row>
			<Row label="Eight tabs" stack>
				<Tabs label="Eight tabs" value={second} onChange={setSecond}>
					{eight.map((label) => (
						<Tab key={label} value={label.toLowerCase()}>
							{label}
						</Tab>
					))}
					{eight.map((label) => (
						<TabPanel key={label} value={label.toLowerCase()}>
							The {label.toLowerCase()} panel.
						</TabPanel>
					))}
				</Tabs>
			</Row>
			<Note>
				Left and Right move between tabs and wrap, Home and End go to the ends,
				and the tab focus reaches is selected. On desktop the track is as wide
				as its tabs, and wraps rather than scroll; on phone it fills the width
				with equal tabs, and more than five tabs are a select.
			</Note>
		</Section>
	);
}
