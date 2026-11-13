import { useMutation, useQuery } from "@tanstack/react-query";
import {
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { ApiError } from "./api/client";
import { Providers } from "./providers";
import { useToast } from "./toast/toast";

afterEach(cleanup);

const failure = new ApiError({
	status: 500,
	title: "The request failed (500)",
	detail: null,
});

function FailingQuery() {
	useQuery({ queryKey: ["failing"], queryFn: () => Promise.reject(failure) });
	return null;
}

function FailingMutation() {
	const mutation = useMutation({ mutationFn: () => Promise.reject(failure) });
	return (
		<button type="button" onClick={() => mutation.mutate()}>
			Save
		</button>
	);
}

describe("a failed call that is not a 401 raises an error toast", () => {
	test("from a query", async () => {
		render(
			<Providers>
				<FailingQuery />
			</Providers>,
		);

		const toast = await screen.findByRole("alert");
		expect(toast.textContent).toContain("Error: The request failed (500)");
	});

	test("from a change", async () => {
		render(
			<Providers>
				<FailingMutation />
			</Providers>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Save" }));

		const toast = await screen.findByRole("alert");
		expect(toast.textContent).toContain("Error: The request failed (500)");
	});
});

function Raise({ texts }: { texts: string[] }) {
	const toast = useToast();
	return (
		<button
			type="button"
			onClick={() => {
				for (const text of texts) toast.error(text);
			}}
		>
			Raise
		</button>
	);
}

describe("an error toast", () => {
	function raise(texts: string[]) {
		render(
			<Providers>
				<Raise texts={texts} />
			</Providers>,
		);
		fireEvent.click(screen.getByRole("button", { name: "Raise" }));
	}

	test("starts with the word Error, once", () => {
		raise(["Saving failed", "Error: already said"]);

		expect(
			screen.getAllByRole("alert").map((alert) => alert.textContent),
		).toEqual(["Error: Saving failedDismiss", "Error: already saidDismiss"]);
	});

	test("stays until it is dismissed", () => {
		raise(["Saving failed"]);

		const toast = screen.getByRole("alert");
		fireEvent.click(within(toast).getByRole("button", { name: "Dismiss" }));

		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("shows at most three, the oldest leaving first", () => {
		raise(["one", "two", "three", "four"]);

		expect(
			screen.getAllByRole("alert").map((alert) => alert.textContent),
		).toEqual([
			"Error: twoDismiss",
			"Error: threeDismiss",
			"Error: fourDismiss",
		]);
	});
});
