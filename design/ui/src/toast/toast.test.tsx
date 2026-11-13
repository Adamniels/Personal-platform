import {
	act,
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	type ToastAction,
	type ToastClock,
	ToastProvider,
	useToast,
} from "../index";

afterEach(cleanup);

/** A clock the test drives: nothing waits, time moves when the test says. */
function fakeClock() {
	let now = 0;
	let next = 1;
	const timers = new Map<number, { at: number; callback: () => void }>();
	const clock: ToastClock & {
		advance: (ms: number) => void;
		pending: () => number;
	} = {
		setTimeout(callback, ms) {
			const id = next++;
			timers.set(id, { at: now + ms, callback });
			return id;
		},
		clearTimeout(handle) {
			timers.delete(handle as number);
		},
		advance(ms) {
			now += ms;
			const due = [...timers].filter(([, timer]) => timer.at <= now);
			due.sort(([, a], [, b]) => a.at - b.at);
			for (const [id, timer] of due) {
				timers.delete(id);
				timer.callback();
			}
		},
		pending: () => timers.size,
	};
	return clock;
}

/** A page that raises toasts from buttons, as a caller does. */
function Raiser({
	text,
	action,
	kind,
}: {
	text: string;
	action?: ToastAction;
	kind: "success" | "error";
}) {
	const toast = useToast();
	return (
		<button type="button" onClick={() => toast[kind](text, action)}>
			Raise {kind} {text}
		</button>
	);
}

function renderToasts(
	toasts: { text: string; kind: "success" | "error"; action?: ToastAction }[],
) {
	const clock = fakeClock();
	render(
		<ToastProvider clock={clock}>
			{toasts.map((toast) => (
				<Raiser key={`${toast.kind} ${toast.text}`} {...toast} />
			))}
		</ToastProvider>,
	);
	const raise = (kind: "success" | "error", text: string) =>
		fireEvent.click(
			screen.getByRole("button", { name: `Raise ${kind} ${text}` }),
		);
	return { clock, raise };
}

describe("a toast is announced", () => {
	it("puts a success in a status region and an error in an alert", () => {
		const { raise } = renderToasts([
			{ kind: "success", text: "Entry saved" },
			{ kind: "error", text: "could not reach the memory service" },
		]);

		raise("success", "Entry saved");
		raise("error", "could not reach the memory service");

		expect(screen.getByRole("status").textContent).toContain("Entry saved");
		expect(screen.getByRole("alert").textContent).toContain(
			"could not reach the memory service",
		);
	});

	it("hides its status dot from assistive technology", () => {
		const { raise } = renderToasts([{ kind: "success", text: "Entry saved" }]);

		raise("success", "Entry saved");

		const dot = screen.getByRole("status").querySelector("[aria-hidden]");
		expect(dot?.getAttribute("aria-hidden")).toBe("true");
		expect(dot?.textContent).toBe("");
	});
});

describe("a success toast leaves after five seconds and an error stays", () => {
	it("removes a success at five seconds and not before", () => {
		const { clock, raise } = renderToasts([
			{ kind: "success", text: "Entry saved" },
		]);
		raise("success", "Entry saved");

		act(() => clock.advance(4999));
		expect(screen.queryByRole("status")).not.toBeNull();

		act(() => clock.advance(1));
		expect(screen.queryByRole("status")).toBeNull();
	});

	it("keeps an error however long passes", () => {
		const { clock, raise } = renderToasts([
			{ kind: "error", text: "could not save" },
		]);
		raise("error", "could not save");

		act(() => clock.advance(60 * 60 * 1000));

		expect(screen.queryByRole("alert")).not.toBeNull();
		expect(clock.pending()).toBe(0);
	});

	it("dismisses an error without an action from its Dismiss button", () => {
		const { raise } = renderToasts([{ kind: "error", text: "could not save" }]);
		raise("error", "could not save");

		fireEvent.click(
			within(screen.getByRole("alert")).getByRole("button", {
				name: "Dismiss",
			}),
		);

		expect(screen.queryByRole("alert")).toBeNull();
	});
});

describe("an error toast always starts with the word Error", () => {
	it.each([
		[
			"could not reach the memory service",
			"Error: could not reach the memory service",
		],
		["Error: the file is too large", "Error: the file is too large"],
		["error the file is too large", "Error: the file is too large"],
	])("shows %j as %j", (text, shown) => {
		const { raise } = renderToasts([{ kind: "error", text }]);

		raise("error", text);

		expect(screen.getByRole("alert").textContent).toContain(shown);
		expect(screen.getByRole("alert").textContent).not.toContain("Error: Error");
	});

	it("does not add the word to a success", () => {
		const { raise } = renderToasts([{ kind: "success", text: "Entry saved" }]);

		raise("success", "Entry saved");

		expect(screen.getByRole("status").textContent).not.toContain("Error");
	});
});

describe("a toast's action reports and dismisses it", () => {
	it.each(["success", "error"] as const)(
		"reports the %s's action once and removes the toast",
		(kind) => {
			const onAction = vi.fn();
			const { clock, raise } = renderToasts([
				{ kind, text: "Entry deleted", action: { label: "Undo", onAction } },
			]);
			raise(kind, "Entry deleted");
			const role = kind === "success" ? "status" : "alert";

			fireEvent.click(
				within(screen.getByRole(role)).getByRole("button", { name: "Undo" }),
			);

			expect(onAction).toHaveBeenCalledOnce();
			expect(screen.queryByRole(role)).toBeNull();
			// The success's timer went with it.
			expect(clock.pending()).toBe(0);
		},
	);

	it("gives each toast one action", () => {
		const { raise } = renderToasts([
			{
				kind: "error",
				text: "could not save",
				action: { label: "Retry", onAction: vi.fn() },
			},
		]);
		raise("error", "could not save");

		const buttons = within(screen.getByRole("alert")).getAllByRole("button");
		expect(buttons.map((button) => button.textContent)).toEqual(["Retry"]);
	});
});

describe("at most three toasts show, the newest lowest", () => {
	it("drops the oldest when a fourth arrives", () => {
		const texts = ["First", "Second", "Third", "Fourth"];
		const { raise } = renderToasts(
			texts.map((text) => ({ kind: "error" as const, text })),
		);

		for (const text of texts) raise("error", text);

		const shown = screen
			.getAllByRole("alert")
			.map((toast) => toast.textContent);
		expect(shown).toHaveLength(3);
		expect(shown[0]).toContain("Second");
		expect(shown[1]).toContain("Third");
		expect(shown[2]).toContain("Fourth");
	});

	it("stops the timer of a success that is dropped", () => {
		const texts = ["First", "Second", "Third", "Fourth"];
		const { clock, raise } = renderToasts(
			texts.map((text) => ({ kind: "success" as const, text })),
		);

		for (const text of texts) raise("success", text);

		expect(clock.pending()).toBe(3);
	});
});

/** A component that asks for toasts with no provider above it. */
function Orphan() {
	useToast();
	return null;
}

describe("useToast needs its provider", () => {
	it("throws outside a ToastProvider", () => {
		const quiet = vi
			.spyOn(console, "error")
			.mockImplementation(() => undefined);
		expect(() => render(<Orphan />)).toThrow(/ToastProvider/);
		quiet.mockRestore();
	});
});
