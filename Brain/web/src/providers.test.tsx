import { useMutation, useQuery } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, test } from "vitest";
import { ApiError } from "./api/client";
import { Providers } from "./providers";

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

function FormChange({ status }: { status: number }) {
	const mutation = useMutation({
		mutationFn: () =>
			Promise.reject(
				new ApiError({ status, title: "Refused", detail: "Too long" }),
			),
		meta: { fieldErrors: true },
	});
	return (
		<button type="button" onClick={() => mutation.mutate()}>
			Save
		</button>
	);
}

describe("a change that shows its refusal on the field", () => {
	function save(status: number) {
		render(
			<Providers>
				<FormChange status={status} />
			</Providers>,
		);
		fireEvent.click(screen.getByRole("button", { name: "Save" }));
	}

	test("raises no toast for a 422", async () => {
		save(422);

		// The failure has settled by the time a later microtask runs.
		await new Promise((resolve) => setTimeout(resolve, 0));
		expect(screen.queryByRole("alert")).toBeNull();
	});

	test("still raises one for any other failure", async () => {
		save(500);

		const toast = await screen.findByRole("alert");
		expect(toast.textContent).toContain("Error: Refused: Too long");
	});
});
