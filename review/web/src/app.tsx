import { EmptyState, Tab, TabPanel, Tabs } from "@platform/ui";
import { type ReactNode, useEffect } from "react";
import { navigate, usePathname } from "./address";
import styles from "./app.module.css";
import { CardsSection } from "./cards/cards-section";
import { DecksSection } from "./decks/decks-section";
import { SECTIONS, type Section, sectionAt } from "./sections";
import { StudySection } from "./study/study-section";

/** What each section shows, by its key. */
const CONTENT: Record<Section["key"], () => ReactNode> = {
	study: () => <StudySection />,
	cards: () => <CardsSection />,
	decks: () => <DecksSection />,
};

function SectionContent({ section }: { section: Section }) {
	return CONTENT[section.key]();
}

/** Review's frame: the page's heading, a tab per section, tied to the
 * address, and the section the address names. */
export function App() {
	const current = sectionAt(usePathname());
	useCrumb(current);

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

/** Names the section in the top bar: the crumb of the page's
 * `<platform-nav>`, which sits outside the React root in index.html, is
 * "Review / " and the section's name, a study session's being Study's. Where
 * the address names no section the attribute is removed, so the bar shows the
 * name of its link, "Review". See "Navigation" in
 * High-level/docs/platform-architecture.md. */
function useCrumb(current: Section | undefined) {
	const label = current?.label;
	useEffect(() => {
		const nav = document.querySelector("platform-nav");
		if (!nav) return;
		if (label) nav.setAttribute("crumb", `Review / ${label}`);
		else nav.removeAttribute("crumb");
	}, [label]);
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
