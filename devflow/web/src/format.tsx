// The one formatting text from the code host gets: `code` becomes a code element and **bold** a
// strong element, as React elements, and everything else stays exactly as written, shown as text.
// No Markdown library, since a pull request's text needs no more than this to read well, and no
// HTML is ever inserted, since anyone who can write on the code host writes this text. See "Text
// read from a code host is data" in devflow/docs/devflow-architecture.md.
import type { ReactNode } from "react";

const TICK = "`";
const BOLD = "**";

/** The text as React nodes: each backtick pair a code element, each pair of
 * double asterisks a strong element, which may hold code. A mark without its
 * closing one, or with nothing between the two, stays as written. */
export function formatInline(text: string): ReactNode[] {
	const nodes: ReactNode[] = [];
	let plain = "";
	let at = 0;
	const flush = () => {
		if (plain !== "") nodes.push(plain);
		plain = "";
	};

	while (at < text.length) {
		if (text.startsWith(TICK, at)) {
			const end = text.indexOf(TICK, at + 1);
			if (end > at + 1) {
				flush();
				nodes.push(<code key={nodes.length}>{text.slice(at + 1, end)}</code>);
				at = end + 1;
				continue;
			}
		} else if (text.startsWith(BOLD, at)) {
			const end = text.indexOf(BOLD, at + BOLD.length);
			if (end > at + BOLD.length) {
				flush();
				nodes.push(
					<strong key={nodes.length}>
						{formatInline(text.slice(at + BOLD.length, end))}
					</strong>,
				);
				at = end + BOLD.length;
				continue;
			}
			// Not bold: both asterisks are text, so the second does not
			// open a pair of its own.
			plain += BOLD;
			at += BOLD.length;
			continue;
		}
		plain += text[at];
		at += 1;
	}
	flush();
	return nodes;
}

/** Text from the code host, formatted as `formatInline` says. */
export function Inline({ text }: { text: string }) {
	return <>{formatInline(text)}</>;
}
