import type { ComponentProps, ReactElement } from "react";
import styles from "./empty-state.module.css";

export type EmptyStateProps = Omit<
	ComponentProps<"div">,
	"title" | "children"
> & {
	/** What is empty, the line in the heading font, in capitals. */
	title: string;
	/** What happens next, the small line under it. */
	detail: string;
	/** At most one action, a secondary `Button` or a link. One element, never
	 * a list of them. */
	action?: ReactElement;
};

/** "Empty state" in section 4 of design/DESIGN.md: two lines and at most one
 * action, centred in a dashed outline, with no illustration. Everything a
 * native div takes passes through. */
export function EmptyState({
	title,
	detail,
	action,
	className,
	...rest
}: EmptyStateProps) {
	return (
		<div
			className={[styles.empty, className].filter(Boolean).join(" ")}
			{...rest}
		>
			<p className={styles.title}>{title}</p>
			<p className={styles.detail}>{detail}</p>
			{action === undefined ? null : (
				<div className={styles.action}>{action}</div>
			)}
		</div>
	);
}
