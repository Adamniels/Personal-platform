import { ToastProvider } from "../src";
import styles from "./gallery.module.css";
import { BadgeSection } from "./sections/badge-section";
import { ButtonSection } from "./sections/button-section";
import { CardSection } from "./sections/card-section";
import { CheckboxSection } from "./sections/checkbox-section";
import { ChipSection } from "./sections/chip-section";
import { DialogSection } from "./sections/dialog-section";
import { EmptyStateSection } from "./sections/empty-state-section";
import { MeterSection } from "./sections/meter-section";
import { ProgressSection } from "./sections/progress-section";
import { SelectSection } from "./sections/select-section";
import { StatusSection } from "./sections/status-section";
import { StepListSection } from "./sections/step-list-section";
import { TableSection } from "./sections/table-section";
import { TabsSection } from "./sections/tabs-section";
import { TextAreaSection } from "./sections/text-area-section";
import { TextFieldSection } from "./sections/text-field-section";
import { TimelineSection } from "./sections/timeline-section";
import { ToastSection } from "./sections/toast-section";
import { ThemeControl } from "./theme-control";

/** Every component of @platform/ui, one section each, in every kind and
 * state. A new component adds its section here. The page is wrapped in the
 * one ToastProvider a page has. */
export function Gallery() {
	return (
		<ToastProvider>
			<div className={styles.page}>
				<header className={styles.header}>
					<div>
						<h1 className={styles.title}>Component gallery</h1>
						<p className={styles.lead}>
							Every component of @platform/ui, in each kind and state, with the
							values from /design/tokens.css.
						</p>
					</div>
					<ThemeControl />
				</header>
				<main className={styles.sections}>
					<ButtonSection />
					<TextFieldSection />
					<TextAreaSection />
					<SelectSection />
					<CheckboxSection />
					<BadgeSection />
					<StatusSection />
					<ChipSection />
					<ProgressSection />
					<MeterSection />
					<TabsSection />
					<CardSection />
					<EmptyStateSection />
					<TimelineSection />
					<StepListSection />
					<TableSection />
					<DialogSection />
					<ToastSection />
				</main>
			</div>
		</ToastProvider>
	);
}
