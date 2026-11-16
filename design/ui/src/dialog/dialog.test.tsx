import {
	act,
	cleanup,
	fireEvent,
	render,
	screen,
	within,
} from "@testing-library/react";
import { useState } from "react";
import {
	afterAll,
	afterEach,
	beforeEach,
	describe,
	expect,
	it,
	vi,
} from "vitest";
import buttonStyles from "../button/button.module.css";
import { Dialog, type DialogKind } from "../index";
import css from "./dialog.module.css?raw";

afterEach(cleanup);

// jsdom has HTMLDialogElement and its `open` attribute, but neither
// showModal nor close. These stand in for the browser's: showModal opens the
// element and moves focus to its first button, as the browser's focusing
// steps do, and close closes it and fires `close`. The top layer, the
// backdrop, inertness and Escape are the browser's own, and are tested in
// e2e/tests/test_gallery.py.
const showModal = vi.fn(function (this: HTMLDialogElement) {
	this.open = true;
	this.querySelector("button")?.focus();
});
const close = vi.fn(function (this: HTMLDialogElement) {
	if (!this.open) return;
	this.open = false;
	this.dispatchEvent(new Event("close"));
});

beforeEach(() => {
	showModal.mockClear();
	close.mockClear();
	HTMLDialogElement.prototype.showModal = showModal;
	HTMLDialogElement.prototype.close = close;
});

afterAll(() => {
	Reflect.deleteProperty(HTMLDialogElement.prototype, "showModal");
	Reflect.deleteProperty(HTMLDialogElement.prototype, "close");
});

function renderDialog(
	props: Partial<{
		kind: DialogKind;
		onConfirm: () => unknown;
		onCancel: () => void;
		open: boolean;
	}> = {},
) {
	const onConfirm = props.onConfirm ?? vi.fn();
	const onCancel = props.onCancel ?? vi.fn();
	const view = render(
		<Dialog
			open={props.open ?? true}
			title="Delete 3 entries?"
			confirmLabel="Delete entries"
			kind={props.kind ?? "destructive"}
			onConfirm={onConfirm}
			onCancel={onCancel}
		>
			<p>They are removed from memory. This cannot be undone.</p>
		</Dialog>,
	);
	return { ...view, onConfirm, onCancel };
}

/** A page with a button that opens the dialog, as a caller writes it: it
 * owns `open`, and closes the dialog on either report. */
function Opener({
	onConfirm = () => undefined,
}: {
	onConfirm?: () => unknown;
}) {
	const [open, setOpen] = useState(false);
	return (
		<>
			<button type="button" onClick={() => setOpen(true)}>
				Delete
			</button>
			<Dialog
				open={open}
				title="Delete 3 entries?"
				confirmLabel="Delete entries"
				kind="destructive"
				onConfirm={async () => {
					await onConfirm();
					setOpen(false);
				}}
				onCancel={() => setOpen(false)}
			>
				<p>They are removed from memory.</p>
			</Dialog>
		</>
	);
}

describe("a dialog is modal and labelled by its title", () => {
	it("opens a native dialog with showModal, labelled by its h2", () => {
		renderDialog();
		const dialog = screen.getByRole("dialog", { name: "Delete 3 entries?" });

		expect(dialog.tagName).toBe("DIALOG");
		expect(showModal).toHaveBeenCalledOnce();
		expect(showModal.mock.contexts[0]).toBe(dialog);
		const title = document.getElementById(
			dialog.getAttribute("aria-labelledby") ?? "",
		);
		expect(title?.tagName).toBe("H2");
		expect(title?.textContent).toBe("Delete 3 entries?");
	});

	it("is not opened, and has no open attribute, while open is false", () => {
		const { container } = renderDialog({ open: false });

		expect(showModal).not.toHaveBeenCalled();
		expect(container.querySelector("dialog")?.hasAttribute("open")).toBe(false);
	});

	it("closes the element when open turns false", () => {
		const { rerender, onConfirm, onCancel } = renderDialog();
		const dialog = screen.getByRole("dialog") as HTMLDialogElement;

		rerender(
			<Dialog
				open={false}
				title="Delete 3 entries?"
				confirmLabel="Delete entries"
				onConfirm={onConfirm}
				onCancel={onCancel}
			/>,
		);

		expect(close).toHaveBeenCalledOnce();
		expect(dialog.open).toBe(false);
		// Closing because the caller asked is not a cancel.
		expect(onCancel).not.toHaveBeenCalled();
	});
});

describe("a dialog puts cancel first and repeats its verb", () => {
	it("has cancel first, then the confirming button with the caller's label", () => {
		renderDialog();
		const buttons = within(screen.getByRole("dialog")).getAllByRole("button");

		expect(buttons.map((button) => button.textContent)).toEqual([
			"Cancel",
			"Delete entries",
		]);
	});

	it("draws cancel as secondary and the confirming button in its kind", () => {
		renderDialog({ kind: "destructive" });
		const [cancel, confirm] = within(screen.getByRole("dialog")).getAllByRole(
			"button",
		);

		expect(cancel?.classList.contains(buttonStyles.secondary as string)).toBe(
			true,
		);
		expect(
			confirm?.classList.contains(buttonStyles.destructive as string),
		).toBe(true);
	});

	it("is primary unless told otherwise", () => {
		render(
			<Dialog
				open
				title="Archive the project?"
				confirmLabel="Archive project"
				onConfirm={vi.fn()}
				onCancel={vi.fn()}
			/>,
		);
		const confirm = screen.getByRole("button", { name: "Archive project" });

		expect(confirm.classList.contains(buttonStyles.primary as string)).toBe(
			true,
		);
	});

	it("aligns the actions to the right", () => {
		const actions = /\.actions\s*\{[^}]*\}/.exec(css)?.[0] ?? "";

		expect(actions).toMatch(/justify-content:\s*flex-end/);
	});
});

describe("Escape and the cancel button both report cancel, and focus returns to what opened it", () => {
	it("reports cancel on Escape, and keeps the dialog for the caller to close", () => {
		const { onCancel } = renderDialog();
		const dialog = screen.getByRole("dialog");

		const cancelEvent = new Event("cancel", { cancelable: true });
		fireEvent(dialog, cancelEvent);

		expect(onCancel).toHaveBeenCalledOnce();
		// The caller owns `open`, so the browser does not close it by itself.
		expect(cancelEvent.defaultPrevented).toBe(true);
	});

	it("reports cancel from the cancel button", () => {
		const { onCancel, onConfirm } = renderDialog();

		fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

		expect(onCancel).toHaveBeenCalledOnce();
		expect(onConfirm).not.toHaveBeenCalled();
	});

	it("reports cancel when the browser closes it on its own", () => {
		const { onCancel } = renderDialog();
		const dialog = screen.getByRole("dialog") as HTMLDialogElement;

		// A browser may close a dialog whose cancel was refused too often.
		dialog.open = false;
		fireEvent(dialog, new Event("close"));

		expect(onCancel).toHaveBeenCalledOnce();
	});

	it.each([
		[
			"the cancel button",
			() => fireEvent.click(screen.getByRole("button", { name: "Cancel" })),
		],
		[
			"Escape",
			() =>
				fireEvent(
					screen.getByRole("dialog"),
					new Event("cancel", { cancelable: true }),
				),
		],
	])("returns focus to the opener after %s", (_how, cancel) => {
		render(<Opener />);
		const opener = screen.getByRole("button", { name: "Delete" });
		opener.focus();
		fireEvent.click(opener);
		expect(document.activeElement).toBe(
			screen.getByRole("button", { name: "Cancel" }),
		);

		cancel();

		expect(document.activeElement).toBe(opener);
	});
});

describe("Tab cycles inside the dialog", () => {
	it("wraps from the last control to the first, and back with Shift", () => {
		renderDialog();
		const cancel = screen.getByRole("button", { name: "Cancel" });
		const confirm = screen.getByRole("button", { name: "Delete entries" });

		confirm.focus();
		const forward = fireEvent.keyDown(confirm, { key: "Tab" });
		expect(document.activeElement).toBe(cancel);
		expect(forward).toBe(false);

		fireEvent.keyDown(cancel, { key: "Tab", shiftKey: true });
		expect(document.activeElement).toBe(confirm);
	});

	it("leaves Tab between the ends to the browser", () => {
		renderDialog();
		const cancel = screen.getByRole("button", { name: "Cancel" });

		cancel.focus();
		const moved = fireEvent.keyDown(cancel, { key: "Tab" });

		// Not prevented, so the browser moves focus on as it always does.
		expect(moved).toBe(true);
		expect(document.activeElement).toBe(cancel);
	});

	it("skips the confirming button while it is disabled", async () => {
		const onConfirm = vi.fn(() => new Promise(() => undefined));
		renderDialog({ onConfirm });
		const cancel = screen.getByRole("button", { name: "Cancel" });
		fireEvent.click(screen.getByRole("button", { name: "Delete entries" }));
		await act(async () => undefined);

		cancel.focus();
		const wrapped = fireEvent.keyDown(cancel, { key: "Tab" });

		// Cancel is the first control and the last, so Tab wraps to itself.
		expect(wrapped).toBe(false);
		expect(document.activeElement).toBe(cancel);
	});
});

describe("a click on the backdrop cancels, and a click inside does not", () => {
	it("cancels on a click on the backdrop, which targets the dialog itself", () => {
		const { onCancel } = renderDialog();
		const dialog = screen.getByRole("dialog");

		fireEvent.mouseDown(dialog);
		fireEvent.click(dialog);

		expect(onCancel).toHaveBeenCalledOnce();
	});

	it("does not cancel on a click inside", () => {
		const { onCancel } = renderDialog();
		const body = screen.getByText(/They are removed/);
		const title = screen.getByRole("heading", { name: "Delete 3 entries?" });

		for (const target of [body, title]) {
			fireEvent.mouseDown(target);
			fireEvent.click(target);
		}

		expect(onCancel).not.toHaveBeenCalled();
	});

	it("does not cancel when a press inside is released on the backdrop", () => {
		const { onCancel } = renderDialog();
		const dialog = screen.getByRole("dialog");

		// Selecting text and letting go outside makes a click on the dialog.
		fireEvent.mouseDown(screen.getByText(/They are removed/));
		fireEvent.click(dialog);

		expect(onCancel).not.toHaveBeenCalled();
	});
});

describe("confirming reports once even when pressed twice", () => {
	it("disables the confirming button while the caller's work runs", async () => {
		let finish: () => void = () => undefined;
		const work = new Promise<void>((resolve) => {
			finish = resolve;
		});
		const onConfirm = vi.fn(() => work);
		renderDialog({ onConfirm });
		const confirm = screen.getByRole("button", { name: "Delete entries" });

		fireEvent.click(confirm);
		fireEvent.click(confirm);

		expect(onConfirm).toHaveBeenCalledOnce();
		expect(confirm).toHaveProperty("disabled", true);

		await act(async () => {
			finish();
			await work;
		});

		expect(confirm).toHaveProperty("disabled", false);
		fireEvent.click(confirm);
		expect(onConfirm).toHaveBeenCalledTimes(2);
	});

	it("reports once for a synchronous confirm pressed twice in one task", async () => {
		const onConfirm = vi.fn();
		renderDialog({ onConfirm });
		const confirm = screen.getByRole("button", { name: "Delete entries" });

		fireEvent.click(confirm);
		fireEvent.click(confirm);

		expect(onConfirm).toHaveBeenCalledOnce();
		await act(async () => undefined);
		expect(confirm).toHaveProperty("disabled", false);
	});

	it("closes and returns focus once the caller's work is done", async () => {
		let finish: () => void = () => undefined;
		const work = new Promise<void>((resolve) => {
			finish = resolve;
		});
		render(<Opener onConfirm={() => work} />);
		const opener = screen.getByRole("button", { name: "Delete" });
		opener.focus();
		fireEvent.click(opener);
		fireEvent.click(screen.getByRole("button", { name: "Delete entries" }));

		await act(async () => {
			finish();
			await work;
		});

		expect(close).toHaveBeenCalledOnce();
		expect(document.activeElement).toBe(opener);
	});
});

describe("a dialog that only shows something has one Close button", () => {
	function renderShowing(onCancel = vi.fn()) {
		render(
			<Dialog open title="devflow-item-desktop" onCancel={onCancel}>
				<p>The screenshot.</p>
			</Dialog>,
		);
		return onCancel;
	}

	it("has Close and nothing to confirm", () => {
		renderShowing();

		const dialog = within(screen.getByRole("dialog"));
		expect(
			dialog.getAllByRole("button").map((button) => button.textContent),
		).toEqual(["Close"]);
		expect(
			dialog.getByRole("heading", { name: "devflow-item-desktop" }),
		).toBeTruthy();
		expect(dialog.getByText("The screenshot.")).toBeTruthy();
	});

	it("reports cancel from Close and on Escape", () => {
		const onCancel = renderShowing();

		fireEvent.click(screen.getByRole("button", { name: "Close" }));
		fireEvent(
			screen.getByRole("dialog"),
			new Event("cancel", { cancelable: true }),
		);

		expect(onCancel).toHaveBeenCalledTimes(2);
	});

	it("draws Close as a secondary button", () => {
		renderShowing();

		const closeButton = screen.getByRole("button", { name: "Close" });
		expect(
			closeButton.classList.contains(buttonStyles.secondary as string),
		).toBe(true);
	});
});
