import { cleanup, render } from "@testing-library/react";
import { createElement, Fragment } from "react";
import { afterEach, describe, expect, test } from "vitest";
import { formatInline } from "./format";

/** The markup the formatted text renders to. */
function html(text: string): string {
	const { container } = render(
		createElement(Fragment, null, ...formatInline(text)),
	);
	return container.innerHTML;
}

afterEach(cleanup);

describe("the inline formatter", () => {
	test("plain text stays as it is", () => {
		expect(html("Adds the vault.")).toBe("Adds the vault.");
	});

	test("backticks become a code element", () => {
		expect(html("Reads `GET /waiting` once.")).toBe(
			"Reads <code>GET /waiting</code> once.",
		);
	});

	test("double asterisks become a strong element", () => {
		expect(html("A **stricter** check.")).toBe(
			"A <strong>stricter</strong> check.",
		);
	});

	test("code inside bold is code", () => {
		expect(html("**Use `fetch`** here")).toBe(
			"<strong>Use <code>fetch</code></strong> here",
		);
	});

	test("bold inside code is left as it is", () => {
		expect(html("`a **b** c`")).toBe("<code>a **b** c</code>");
	});

	test("an unclosed backtick stays a backtick", () => {
		expect(html("one ` two")).toBe("one ` two");
	});

	test("unclosed or empty asterisks stay as they are", () => {
		expect(html("a ** b")).toBe("a ** b");
		expect(html("a **** b")).toBe("a **** b");
	});

	test("a URL is text, never a link", () => {
		const { container } = render(
			createElement(
				Fragment,
				null,
				...formatInline("See https://example.com/a?b=1 for it."),
			),
		);
		expect(container.querySelector("a")).toBeNull();
		expect(container.textContent).toBe("See https://example.com/a?b=1 for it.");
	});

	test("markup is shown as text and never becomes an element", () => {
		const { container } = render(
			createElement(
				Fragment,
				null,
				...formatInline(
					'<script>alert(1)</script> <img src=x onerror="alert(2)"> `<b>x</b>`',
				),
			),
		);
		expect(container.querySelector("script, img, b")).toBeNull();
		expect(container.textContent).toBe(
			'<script>alert(1)</script> <img src=x onerror="alert(2)"> <b>x</b>',
		);
	});

	test("other Markdown is left alone", () => {
		expect(html("# Title, _italic_, *one*, [a](b)")).toBe(
			"# Title, _italic_, *one*, [a](b)",
		);
	});
});
