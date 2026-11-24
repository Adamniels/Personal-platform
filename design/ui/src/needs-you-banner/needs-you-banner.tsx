import { TriangleAlert } from "lucide-react";
import type { ComponentProps, ReactElement, ReactNode } from "react";
import styles from "./needs-you-banner.module.css";

export type NeedsYouBannerProps = Omit<ComponentProps<"div">, "children"> & {
	/** The question, or what is missing, at weight 600. */
	message: string;
	/** An optional line of consequence, in --text-2. */
	detail?: ReactNode;
	/** At most one action, the recommended one; one element, never a list. */
	action?: ReactElement;
};

/** "Needs you banner" in section 4 of design/DESIGN.md: a warning icon, the
 * words "Needs you", the message and an optional line of consequence, and
 * its action last. One per view; the card that holds it takes the accent
 * border itself. Everything a native div takes passes through. */
export function NeedsYouBanner({
	message,
	detail,
	action,
	className,
	...rest
}: NeedsYouBannerProps) {
	const classes = [styles.banner, className].filter(Boolean).join(" ");
	return (
		<div className={classes} {...rest}>
			<TriangleAlert className={styles.icon} size={18} aria-hidden="true" />
			<div className={styles.text}>
				<p className={styles.words}>Needs you</p>
				<p className={styles.message}>{message}</p>
				{detail === undefined ? null : (
					<p className={styles.detail}>{detail}</p>
				)}
				{action === undefined ? null : (
					<div className={styles.action}>{action}</div>
				)}
			</div>
		</div>
	);
}
