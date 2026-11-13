import type { ComponentProps, ReactNode } from "react";
import styles from "./status.module.css";

export type BadgeProps = Omit<ComponentProps<"span">, "children"> & {
	/** A category or a count. */
	children: ReactNode;
};

/** A badge for categories and counts, from "Badge and status label" in
 * section 4 of design/DESIGN.md: always the neutral fill, never a dot and
 * never a tone, which is what tells it apart from a status. */
export function Badge({ className, ...rest }: BadgeProps) {
	return (
		<span
			className={[styles.label, styles.badge, className]
				.filter(Boolean)
				.join(" ")}
			{...rest}
		/>
	);
}
