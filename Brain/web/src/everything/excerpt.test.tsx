import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { Excerpt } from "./excerpt";

afterEach(cleanup);

function shown(html: string) {
	const { container } = render(<Excerpt html={html} />);
	return container;
}

describe("a search excerpt renders its marked words and nothing else as markup", () => {
	test("<mark> becomes emphasis", () => {
		const excerpt = shown(
			"Learning <mark>Rust</mark> by building the <mark>brain</mark>",
		);

		const marked = [...excerpt.querySelectorAll("mark")].map(
			(mark) => mark.textContent,
		);
		expect(marked).toEqual(["Rust", "brain"]);
		expect(excerpt.textContent).toBe("Learning Rust by building the brain");
	});

	test("any other tag in the text shows as text", () => {
		// The brain escapes every character but the marks, so a tag in what
		// was written arrives as entities, and is shown as written.
		const excerpt = shown(
			"Wrote &lt;b&gt;bold&lt;/b&gt; &amp; &quot;quoted&quot; &#x27;<mark>text</mark>&#x27;",
		);

		expect(excerpt.querySelector("b")).toBeNull();
		expect(excerpt.querySelectorAll("mark")).toHaveLength(1);
		expect(excerpt.textContent).toBe(`Wrote <b>bold</b> & "quoted" 'text'`);
	});

	test("a tag that is not escaped is never parsed", () => {
		const excerpt = shown(
			'<img src=x onerror="alert(1)"><script>alert(2)</script><mark>word</mark>',
		);

		expect(excerpt.querySelector("img")).toBeNull();
		expect(excerpt.querySelector("script")).toBeNull();
		expect(excerpt.textContent).toBe(
			'<img src=x onerror="alert(1)"><script>alert(2)</script>word',
		);
	});

	test("a mark left open or a stray close is text around it", () => {
		const excerpt = shown("an <mark>open mark and a </mark></mark> stray");

		expect(
			[...excerpt.querySelectorAll("mark")].map((m) => m.textContent),
		).toEqual(["open mark and a "]);
		expect(excerpt.textContent).toBe("an open mark and a </mark> stray");
	});
});
