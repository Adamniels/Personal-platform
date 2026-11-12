import { useEffect, useId, useState } from "react";
import styles from "./theme-control.module.css";

type Theme = "system" | "light" | "dark";

const OPTIONS: { value: Theme; label: string }[] = [
	{ value: "system", label: "Follow system" },
	{ value: "light", label: "Light" },
	{ value: "dark", label: "Dark" },
];

/** Sets data-theme on the root element, which is all dark mode is: the
 * values in tokens.css change, and no component holds a theme condition. */
export function ThemeControl() {
	const id = useId();
	const [theme, setTheme] = useState<Theme>("system");

	useEffect(() => {
		const root = document.documentElement;
		if (theme === "system") root.removeAttribute("data-theme");
		else root.setAttribute("data-theme", theme);
	}, [theme]);

	return (
		<div className={styles.field}>
			<label htmlFor={id} className={styles.label}>
				Theme
			</label>
			<select
				id={id}
				className={styles.select}
				value={theme}
				onChange={(event) => setTheme(event.target.value as Theme)}
			>
				{OPTIONS.map((option) => (
					<option key={option.value} value={option.value}>
						{option.label}
					</option>
				))}
			</select>
		</div>
	);
}
