import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import type { Status } from "./banner";
import { CodeHostBanner } from "./banner";

const TEXT = "Fake GitHub. Nothing here is real.";

afterEach(cleanup);

/** Renders the banner over a status that answers with `answer`, and waits
 * for it to settle. */
async function renderWith(answer: Promise<Status>) {
	render(<CodeHostBanner loadStatus={() => answer} />);
	await act(async () => {
		await answer.catch(() => undefined);
	});
}

describe("the banner says when GitHub is faked", () => {
	test("it shows in the fake mode", async () => {
		await renderWith(Promise.resolve({ code_host: "fake" }));

		const banner = screen.getByRole("note");
		expect(banner.textContent).toBe(TEXT);
		// Not dismissible: there is nothing to press.
		expect(screen.queryByRole("button")).toBeNull();
	});

	test("it does not show with GitHub", async () => {
		await renderWith(Promise.resolve({ code_host: "github" }));

		expect(screen.queryByRole("note")).toBeNull();
		expect(screen.queryByText(TEXT)).toBeNull();
	});

	test("it does not show before the status answers, or when it fails", async () => {
		render(<CodeHostBanner loadStatus={() => new Promise<Status>(() => {})} />);
		expect(screen.queryByText(TEXT)).toBeNull();
		cleanup();

		await renderWith(Promise.reject(new Error("unreachable")));
		expect(screen.queryByText(TEXT)).toBeNull();
	});
});
