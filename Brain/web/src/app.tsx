import { EmptyState, Tab, TabPanel, Tabs } from "@platform/ui";
import { navigate, usePathname } from "./address";
import styles from "./app.module.css";
import { Everything } from "./everything/everything";
import { History } from "./history/history";
import { Knowledge } from "./knowledge/knowledge";
import { Now } from "./now/now";
import { Profile } from "./profile/profile";
import { Rules } from "./rules/rules";
import { SECTIONS, type Section, sectionAt } from "./sections";

/** The memory centre's frame: the page's heading, a tab per section, tied to
 * the address, and the section the address names. */
export function App() {
	const current = sectionAt(usePathname());

	return (
		<main className="page">
			<header className={styles.header}>
				<h1 className={styles.title}>Memory</h1>
				<p className={styles.lede}>
					What the platform knows about you. Correct anything that is wrong.
				</p>
			</header>
			<Tabs
				label="Memory sections"
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
						{section === current ? <SectionView section={section} /> : null}
					</TabPanel>
				))}
			</Tabs>
			{current ? null : <NotFound />}
		</main>
	);
}

/** The section the address names. Until its slice lands, a section says it
 * is empty. */
function SectionView({ section }: { section: Section }) {
	if (section.key === "profile") return <Profile />;
	if (section.key === "rules") return <Rules />;
	if (section.key === "knowledge") return <Knowledge />;
	if (section.key === "now") return <Now />;
	if (section.key === "history") return <History />;
	if (section.key === "everything") return <Everything />;
	return (
		<EmptyState
			title={`Nothing in ${section.label} yet`}
			detail="This part of the memory centre is still being built."
		/>
	);
}

function NotFound() {
	return (
		<EmptyState
			className={styles.missing}
			title="There is no such section"
			detail="The address names no part of the memory centre."
			action={
				<a className={styles.link} href="/brain">
					Go to Profile
				</a>
			}
		/>
	);
}
