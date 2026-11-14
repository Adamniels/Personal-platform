import { EmptyState, Tab, TabPanel, Tabs } from "@platform/ui";
import type { ReactNode } from "react";
import { navigate, usePathname } from "./address";
import styles from "./app.module.css";
import { DecksSection } from "./decks/decks-section";
import { SECTIONS, type Section, sectionAt } from "./sections";

/** What each section shows, by its key; a section not here says it is still
 * being built. */
const CONTENT: Record<string, () => ReactNode> = {
	decks: () => <DecksSection />,
};

function SectionContent({ section }: { section: Section }) {
	const content = CONTENT[section.key];
	if (content) return content();
	return (
		<EmptyState
			title={section.empty}
			detail="This part of review is still being built."
		/>
	);
}

/** Review's frame: the page's heading, a tab per section, tied to the
 * address, and the section the address names. */
export function App() {
	const current = sectionAt(usePathname());

	return (
		<main className="page">
			<header className={styles.header}>
				<h1 className={styles.title}>Review</h1>
				<p className={styles.lede}>
					Cards that come back to you, so what you mark as worth remembering
					sticks.
				</p>
			</header>
			<Tabs
				label="Review sections"
				value={current?.key ?? ""}
				onChange={(key) => {
					const next = SECTIONS.find((section) => section.key === key);
					if (next) navigate(next.path);
				}}
			>
				{SECTIONS.map((section) => (
					<Tab key={section.key} value={section.key}>
						{section.label}
					</Tab>
				))}
				{SECTIONS.map((section) => (
					<TabPanel key={section.key} value={section.key}>
						{section === current ? <SectionContent section={section} /> : null}
					</TabPanel>
				))}
			</Tabs>
			{current ? null : <NotFound />}
		</main>
	);
}

function NotFound() {
	return (
		<EmptyState
			className={styles.missing}
			title="There is no such section"
			detail="The address names no part of review."
			action={
				<a className={styles.link} href="/review">
					Go to Study
				</a>
			}
		/>
	);
}
