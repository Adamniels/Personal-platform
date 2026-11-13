// A search excerpt, as GET /search gives it: HTML in which the matched words are in <mark> and
// every other character is escaped, as "Text search" in Brain/README.md has it. It is never set as
// HTML. It is split on <mark> and </mark>, and everything else is text, with the five entities the
// brain escapes read back, so a tag in what was written shows as written and nothing else in it
// is ever markup.
import type { ComponentProps } from "react";

const OPEN = "<mark>";
const CLOSE = "</mark>";

/** What the brain's escaping writes for each character, `html.escape`'s
 * five. */
const ENTITIES: Record<string, string> = {
	"&amp;": "&",
	"&lt;": "<",
	"&gt;": ">",
	"&quot;": '"',
	"&#x27;": "'",
};

function decoded(text: string): string {
	return text.replace(/&(?:amp|lt|gt|quot|#x27);/g, (entity) => {
		return ENTITIES[entity] ?? entity;
	});
}

export type ExcerptPart = { text: string; marked: boolean };

/** The excerpt's runs of text, each marked or not. A <mark> opens one only
 * outside a mark, and a </mark> closes one only inside it; any other is
 * text, and so is a mark left open at the end. */
export function excerptParts(html: string): ExcerptPart[] {
	const parts: ExcerptPart[] = [];
	let rest = html;
	let text = "";
	while (rest !== "") {
		const open = rest.indexOf(OPEN);
		if (open === -1) break;
		const close = rest.indexOf(CLOSE, open + OPEN.length);
		if (close === -1) break;
		text += rest.slice(0, open);
		if (text !== "") parts.push({ text: decoded(text), marked: false });
		text = "";
		parts.push({
			text: decoded(rest.slice(open + OPEN.length, close)),
			marked: true,
		});
		rest = rest.slice(close + CLOSE.length);
	}
	text += rest;
	if (text !== "") parts.push({ text: decoded(text), marked: false });
	return parts;
}

/** The excerpt, its matched words emphasised in a `mark`. */
export function Excerpt({
	html,
	...rest
}: { html: string } & Omit<ComponentProps<"p">, "children">) {
	return (
		<p {...rest}>
			{excerptParts(html).map((part, index) =>
				part.marked ? (
					// The runs never move, so their place is their key.
					// biome-ignore lint/suspicious/noArrayIndexKey: see above
					<mark key={index}>{part.text}</mark>
				) : (
					// biome-ignore lint/suspicious/noArrayIndexKey: see above
					<span key={index}>{part.text}</span>
				),
			)}
		</p>
	);
}
