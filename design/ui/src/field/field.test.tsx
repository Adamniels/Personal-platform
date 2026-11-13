import { cleanup, render, screen } from "@testing-library/react";
import { createRef, type ReactElement } from "react";
import { afterEach, describe, expect, it } from "vitest";
import { Select, TextArea, TextField } from "../index";

afterEach(cleanup);

// The three fields share one rule in section 4 of design/DESIGN.md, so each
// test runs against all of them. `role` is how the control is found.
type Case = {
	name: string;
	role: "textbox" | "combobox";
	tag: string;
	render: (props: {
		label: string;
		help?: string;
		error?: string;
	}) => ReactElement;
};

const fields: Case[] = [
	{
		name: "TextField",
		role: "textbox",
		tag: "INPUT",
		render: (props) => <TextField {...props} />,
	},
	{
		name: "TextArea",
		role: "textbox",
		tag: "TEXTAREA",
		render: (props) => <TextArea {...props} />,
	},
	{
		name: "Select",
		role: "combobox",
		tag: "SELECT",
		render: (props) => (
			<Select {...props}>
				<option value="">Choose one</option>
				<option value="a">A</option>
			</Select>
		),
	},
];

/** The text of the elements aria-describedby names, in its order, as an
 * assistive technology reads it. */
function description(element: HTMLElement): string {
	const ids = (element.getAttribute("aria-describedby") ?? "")
		.split(/\s+/)
		.filter(Boolean);
	return ids
		.map((id) => document.getElementById(id)?.textContent ?? "")
		.join(" ");
}

describe("a field ties its label, help and error to the input", () => {
	it.each(fields)("%s: the label is the accessible name", (field) => {
		render(field.render({ label: "Title" }));

		const control = screen.getByRole(field.role, { name: "Title" });
		expect(control.tagName).toBe(field.tag);
		expect(control.getAttribute("aria-invalid")).toBeNull();
		expect(control.hasAttribute("aria-describedby")).toBe(false);
	});

	it.each(fields)("%s: help is the description", (field) => {
		render(field.render({ label: "Title", help: "Shown on the card" }));

		const control = screen.getByRole(field.role, { name: "Title" });
		expect(description(control)).toBe("Shown on the card");
		expect(control.getAttribute("aria-invalid")).toBeNull();
	});

	it.each(fields)("%s: an error is invalid and the description", (field) => {
		render(field.render({ label: "Title", error: "it is empty" }));

		const control = screen.getByRole(field.role, { name: "Title" });
		expect(control.getAttribute("aria-invalid")).toBe("true");
		expect(description(control)).toBe("Error: it is empty");
	});

	it.each(fields)(
		"%s: with help and an error, the error is read first",
		(field) => {
			render(
				field.render({
					label: "Title",
					help: "Shown on the card",
					error: "it is empty",
				}),
			);

			const control = screen.getByRole(field.role, { name: "Title" });
			expect(control.getAttribute("aria-invalid")).toBe("true");
			expect(description(control)).toBe("Error: it is empty Shown on the card");
		},
	);
});

describe("an error message always starts with the word Error", () => {
	it("adds the word to what is wrong", () => {
		render(<TextField label="Password" error="use at least 12 characters" />);

		expect(screen.getByText("Error: use at least 12 characters")).toBeDefined();
	});

	it.each([
		"Error: use at least 12 characters",
		"error: use at least 12 characters",
		"Error use at least 12 characters",
	])("does not double it in %j", (error) => {
		render(<TextField label="Password" error={error} />);

		expect(screen.getByText("Error: use at least 12 characters")).toBeDefined();
	});

	it("leaves a word that only begins with Error alone", () => {
		render(<TextField label="Log" error="Errors were found in the file" />);

		expect(
			screen.getByText("Error: Errors were found in the file"),
		).toBeDefined();
	});
});

describe("a field passes native props through and forwards its ref", () => {
	it("TextField", () => {
		const ref = createRef<HTMLInputElement>();
		render(
			<TextField
				ref={ref}
				label="Email"
				type="email"
				name="email"
				placeholder="you@example.com"
				required
				disabled
				defaultValue="a@b.se"
				className="extra"
			/>,
		);
		const input = screen.getByRole("textbox", { name: "Email" });

		expect(ref.current).toBe(input);
		expect(input.getAttribute("type")).toBe("email");
		expect(input.getAttribute("name")).toBe("email");
		expect(input.getAttribute("placeholder")).toBe("you@example.com");
		expect(input).toHaveProperty("required", true);
		expect(input).toHaveProperty("disabled", true);
		expect(input).toHaveProperty("value", "a@b.se");
		expect(input.classList.contains("extra")).toBe(true);
	});

	it("TextArea", () => {
		const ref = createRef<HTMLTextAreaElement>();
		render(
			<TextArea
				ref={ref}
				label="Notes"
				name="notes"
				rows={6}
				maxLength={200}
				defaultValue="First line"
			/>,
		);
		const textarea = screen.getByRole("textbox", { name: "Notes" });

		expect(ref.current).toBe(textarea);
		expect(textarea.getAttribute("name")).toBe("notes");
		expect(textarea.getAttribute("rows")).toBe("6");
		expect(textarea.getAttribute("maxlength")).toBe("200");
		expect(textarea).toHaveProperty("value", "First line");
	});

	it("Select", () => {
		const ref = createRef<HTMLSelectElement>();
		render(
			<Select ref={ref} label="Project" name="project" defaultValue="b">
				<option value="a">Alpha</option>
				<option value="b">Beta</option>
			</Select>,
		);
		const select = screen.getByRole("combobox", { name: "Project" });

		expect(ref.current).toBe(select);
		expect(select.getAttribute("name")).toBe("project");
		expect(select).toHaveProperty("value", "b");
		expect(screen.getAllByRole("option")).toHaveLength(2);
	});

	it("keeps an id and a description the caller gives", () => {
		render(
			<>
				<p id="extra">More about it</p>
				<TextField
					label="Title"
					id="title"
					aria-describedby="extra"
					error="it is empty"
				/>
			</>,
		);
		const input = screen.getByRole("textbox", { name: "Title" });

		expect(input.id).toBe("title");
		expect(description(input)).toBe("Error: it is empty More about it");
	});
});
