import { EmptyState, Tab, TabPanel, Tabs } from "@platform/ui";
import { navigate, usePathname } from "./address";
import styles from "./app.module.css";
import { SECTIONS, sectionAt } from "./sections";

/** Dev flow's frame: the page's heading, a tab per section, tied to the
 * address, and the section the address names. */
export function App() {
	const current = sectionAt(usePathname());

	return (
		<main className="page">
			<header className={styles.header}>
				<h1 className={styles.title}>Dev flow</h1>
				<p className={styles.lede}>
					What your agents built, and what waits for you.
				</p>
			</header>
			<Tabs
				label="Dev flow sections"
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
						{section === current ? (
							<EmptyState
								title={section.empty.title}
								detail={section.empty.detail}
							/>
						) : null}
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
			detail="The address names no part of Dev flow."
			action={
				<a className={styles.link} href="/devflow">
					Go to Waiting for you
				</a>
			}
		/>
	);
}
