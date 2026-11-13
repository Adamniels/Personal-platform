import type { ComponentProps, MouseEvent } from "react";
import { navigate } from "./address";

/** A link within the application: a real `a`, so it can be opened in a new
 * tab or copied, that goes to its address through the History API when it
 * is clicked plainly. */
export function Link({
	href,
	onClick,
	...rest
}: ComponentProps<"a"> & { href: string }) {
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
	return <a href={href} onClick={follow} {...rest} />;
}
