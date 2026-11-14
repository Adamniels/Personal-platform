// A link to an address of review's own, followed in place through src/address.ts, so the page does
// not load again, while a click with a modifier, or the middle button, still opens it as the browser
// would. It is a real `a` with its `href`, so it can be opened in a new tab and is read as a link.
//
// `primary` and `secondary` draw it as "Button" in section 4 of design/DESIGN.md draws those kinds,
// for an action that goes to an address, such as Start. It stays here rather than in @platform/ui
// until a second feature needs it, as design/ui/README.md says of a component one feature uses.
import type { ComponentProps, MouseEvent } from "react";
import { navigate } from "../address";
import styles from "./app-link.module.css";

export type AppLinkKind = "primary" | "secondary" | "text";

export type AppLinkProps = Omit<ComponentProps<"a">, "href"> & {
	/** A path of review's, such as /review/study?deck=<id>. */
	href: string;
	kind?: AppLinkKind;
};

export function AppLink({
	href,
	kind = "text",
	className,
	onClick,
	...rest
}: AppLinkProps) {
	function follow(event: MouseEvent<HTMLAnchorElement>) {
		onClick?.(event);
		if (
			event.defaultPrevented ||
			event.button !== 0 ||
			event.metaKey ||
			event.ctrlKey ||
			event.shiftKey ||
			event.altKey
		) {
			return;
		}
		event.preventDefault();
		navigate(href);
	}

	const classes = [
		kind === "text" ? styles.text : [styles.button, styles[kind]].join(" "),
		className,
	]
		.filter(Boolean)
		.join(" ");
	return <a href={href} className={classes} onClick={follow} {...rest} />;
}
