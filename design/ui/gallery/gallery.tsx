import styles from "./gallery.module.css";
import { ButtonSection } from "./sections/button-section";
import { ThemeControl } from "./theme-control";

/** Every component of @platform/ui, one section each, in every kind and
 * state. A new component adds its section here. */
export function Gallery() {
	return (
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
			</main>
		</div>
	);
}
