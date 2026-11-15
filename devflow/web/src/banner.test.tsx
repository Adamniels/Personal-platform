import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { CodeHostBanner } from "./banner";

const TEXT = "Fake GitHub. Nothing here is real.";
const UNLINKED = { state: "none", login: null } as const;

afterEach(cleanup);

describe("the banner says when GitHub is faked", () => {
	test("it shows in the fake mode", () => {
		render(<CodeHostBanner status={{ code_host: "fake", github: UNLINKED }} />);

		const banner = screen.getByRole("note");
		expect(banner.textContent).toBe(TEXT);
		// Not dismissible: there is nothing to press.
		expect(screen.queryByRole("button")).toBeNull();
	});

	test("it does not show with GitHub", () => {
		render(
			<CodeHostBanner status={{ code_host: "github", github: UNLINKED }} />,
		);

		expect(screen.queryByRole("note")).toBeNull();
		expect(screen.queryByText(TEXT)).toBeNull();
	});

	test("it does not show before the status answers, or when it cannot be read", () => {
		render(<CodeHostBanner status={undefined} />);

		expect(screen.queryByText(TEXT)).toBeNull();
	});
});
