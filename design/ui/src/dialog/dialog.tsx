import {
	type ComponentProps,
	type KeyboardEvent,
	type MouseEvent,
	type ReactNode,
	useEffect,
	useId,
	useRef,
	useState,
} from "react";
import { Button, type ButtonKind } from "../button/button";
import styles from "./dialog.module.css";

/** What Tab can reach inside the dialog. */
const TABBABLE =
	"a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]";

export type DialogKind = Extract<ButtonKind, "primary" | "destructive">;

/** A dialog that asks for an action: Cancel, then the confirming button. */
type Confirming = {
	/** The confirming button, which repeats the title's verb: "Delete
	 * entries". */
	confirmLabel: string;
	/** The confirming button's kind: primary (the default) or destructive. */
	kind?: DialogKind;
	/** Called when the confirming button is pressed. It may return a promise:
	 * while that is pending the button is disabled, so a second press does
	 * nothing. Any other return value is ignored. */
	onConfirm: () => unknown;
};

/** A dialog that only shows something, such as an image: one Close button,
 * which reports cancel, and nothing to confirm. */
type Showing = {
	confirmLabel?: undefined;
	kind?: undefined;
	onConfirm?: undefined;
};

export type DialogProps = Omit<
	ComponentProps<"dialog">,
	| "open"
	| "title"
	| "children"
	| "onCancel"
	| "onClose"
	| "onClick"
	| "onMouseDown"
	| "onKeyDown"
	| "aria-labelledby"
> & {
	/** Whether it is shown. The caller owns it, and closes the dialog on
	 * either report. */
	open: boolean;
	/** The h2: the action stated as a question, "Delete 3 entries?", or
	 * for a dialog that only shows something, the name of what it shows. */
	title: string;
	/** The body: what happens, and what cannot be undone; or what it shows. */
	children?: ReactNode;
	/** Called on the cancel or Close button, Escape, and a click on the
	 * backdrop. */
	onCancel: () => void;
} & (Confirming | Showing);

/** "Dialog" in section 4 of design/DESIGN.md, on the native dialog element.
 * It asks for an action, with Cancel and a confirming button, or, without
 * confirmLabel and onConfirm, only shows something, with one Close button:
 * showModal gives the top layer, the backdrop and Escape, and makes the page
 * behind it inert; Tab wraps at its ends, so focus stays inside. Below 640px it is a sheet at the
 * bottom. Focus returns to what had it when the dialog opened. */
export function Dialog({
	open,
	title,
	children,
	confirmLabel,
	kind = "primary",
	onConfirm,
	onCancel,
	className,
	...rest
}: DialogProps) {
	const ref = useRef<HTMLDialogElement>(null);
	const titleId = useId();
	const [pending, setPending] = useState(false);
	// Set at once on a press, since the disabled button only renders later.
	const confirming = useRef(false);
	// Whether the press that ended in a click began on the backdrop.
	const pressedBackdrop = useRef(false);

	useEffect(() => {
		const dialog = ref.current;
		if (!open || !dialog) return;
		const opener = document.activeElement;
		if (!dialog.open) dialog.showModal();
		return () => {
			if (dialog.open) dialog.close();
			if (opener instanceof HTMLElement && opener.isConnected) opener.focus();
		};
	}, [open]);

	function confirm() {
		if (confirming.current || onConfirm === undefined) return;
		confirming.current = true;
		setPending(true);
		let work: Promise<unknown>;
		try {
			work = Promise.resolve(onConfirm());
		} catch (error) {
			work = Promise.reject(error);
		}
		// A failure is the caller's to handle, so it is not swallowed here.
		void work.finally(() => {
			confirming.current = false;
			setPending(false);
		});
	}

	function wrapTab(event: KeyboardEvent<HTMLDialogElement>) {
		// The modal dialog makes the page inert, but Tab from its last control
		// leaves for the browser's own interface. It wraps instead.
		if (event.key !== "Tab") return;
		const reachable = [
			...event.currentTarget.querySelectorAll<HTMLElement>(TABBABLE),
		].filter((element) => element.tabIndex >= 0);
		const first = reachable[0];
		const last = reachable.at(-1);
		if (!first || !last) return;
		const active = document.activeElement;
		if (
			event.shiftKey &&
			(active === first || active === event.currentTarget)
		) {
			event.preventDefault();
			last.focus();
		} else if (!event.shiftKey && active === last) {
			event.preventDefault();
			first.focus();
		}
	}

	function backdropPress(event: MouseEvent<HTMLDialogElement>) {
		pressedBackdrop.current = event.target === event.currentTarget;
	}

	function backdropClick(event: MouseEvent<HTMLDialogElement>) {
		// The content fills the dialog's box, so only the backdrop targets the
		// dialog itself. A press inside released outside is not a click on it.
		const outside =
			event.target === event.currentTarget && pressedBackdrop.current;
		pressedBackdrop.current = false;
		if (outside) onCancel();
	}

	return (
		<dialog
			{...rest}
			ref={ref}
			aria-labelledby={titleId}
			className={[styles.dialog, className].filter(Boolean).join(" ")}
			onCancel={(event) => {
				// Escape. The caller closes it, by passing open={false}.
				event.preventDefault();
				onCancel();
			}}
			onClose={() => {
				// Closed by the browser while the caller still has it open.
				if (open && !ref.current?.open) onCancel();
			}}
			onKeyDown={wrapTab}
			onMouseDown={backdropPress}
			onClick={backdropClick}
		>
			<div className={styles.content}>
				<h2 id={titleId} className={styles.title}>
					{title}
				</h2>
				{children !== undefined && (
					<div className={styles.body}>{children}</div>
				)}
				<div className={styles.actions}>
					{confirmLabel === undefined ? (
						<Button onClick={onCancel}>Close</Button>
					) : (
						<>
							<Button onClick={onCancel}>Cancel</Button>
							<Button kind={kind} disabled={pending} onClick={confirm}>
								{confirmLabel}
							</Button>
						</>
					)}
				</div>
			</div>
		</dialog>
	);
}
