import {
	createContext,
	type ReactNode,
	useCallback,
	useContext,
	useEffect,
	useMemo,
	useRef,
	useState,
} from "react";
import { errorText } from "../field/field";
import styles from "./toast.module.css";

/** The one text action a toast may carry, such as "Undo" or "Retry".
 * Pressing it reports and dismisses the toast. */
export type ToastAction = {
	label: string;
	onAction: () => void;
};

/** Where a toast's timer comes from. The provider uses the window's unless
 * given another, which is how a test drives time without waiting. */
export type ToastClock = {
	setTimeout: (callback: () => void, ms: number) => unknown;
	clearTimeout: (handle: unknown) => void;
};

/** What useToast gives a page. */
export type Toasts = {
	/** Shows a success, which leaves after five seconds. */
	success: (text: string, action?: ToastAction) => void;
	/** Shows an error, which stays until dismissed. The word "Error" is added
	 * unless the text already starts with it. */
	error: (text: string, action?: ToastAction) => void;
};

export type ToastProviderProps = {
	children?: ReactNode;
	/** Read once, when the provider mounts. */
	clock?: ToastClock;
};

type ToastKind = "success" | "error";

type Toast = {
	id: number;
	kind: ToastKind;
	text: string;
	action: ToastAction | undefined;
};

/** How long a success stays, from "Toast" in section 4 of DESIGN.md. */
const SUCCESS_MS = 5000;

/** The most toasts on screen at once. A new one past it drops the oldest. */
const MAX_TOASTS = 3;

const windowClock: ToastClock = {
	setTimeout: (callback, ms) => window.setTimeout(callback, ms),
	clearTimeout: (handle) => window.clearTimeout(handle as number),
};

const ToastContext = createContext<Toasts | null>(null);

/** Shows the toasts a page raises through useToast, at most three, the
 * newest lowest, bottom right on desktop and above the tab bar on phone. A
 * page wraps itself in one, once. */
export function ToastProvider({
	children,
	clock: givenClock = windowClock,
}: ToastProviderProps) {
	// Kept from the first render, so a clock written inline does not restart
	// every timer each time the page renders.
	const [clock] = useState(givenClock);
	const [toasts, setToasts] = useState<Toast[]>([]);
	const nextId = useRef(1);

	const dismiss = useCallback((id: number) => {
		setToasts((shown) => shown.filter((toast) => toast.id !== id));
	}, []);

	const toastsApi = useMemo<Toasts>(() => {
		const add = (kind: ToastKind, text: string, action?: ToastAction) => {
			const toast = { id: nextId.current++, kind, text, action };
			setToasts((shown) => [...shown, toast].slice(-MAX_TOASTS));
		};
		return {
			success: (text, action) => add("success", text, action),
			error: (text, action) => add("error", text, action),
		};
	}, []);

	return (
		<ToastContext.Provider value={toastsApi}>
			{children}
			{toasts.length > 0 && (
				<div className={styles.stack}>
					{toasts.map((toast) => (
						<ToastItem
							key={toast.id}
							toast={toast}
							clock={clock}
							onDismiss={dismiss}
						/>
					))}
				</div>
			)}
		</ToastContext.Provider>
	);
}

/** The success and error of the nearest ToastProvider. */
export function useToast(): Toasts {
	const toasts = useContext(ToastContext);
	if (!toasts) {
		throw new Error("useToast needs a ToastProvider above it");
	}
	return toasts;
}

/** "Toast" in section 4 of design/DESIGN.md: a dot, one line of text and
 * one text action. An error without an action gets "Dismiss", so every
 * error can be closed. */
function ToastItem({
	toast,
	clock,
	onDismiss,
}: {
	toast: Toast;
	clock: ToastClock;
	onDismiss: (id: number) => void;
}) {
	const { id, kind, text } = toast;

	useEffect(() => {
		if (kind !== "success") return;
		const handle = clock.setTimeout(() => onDismiss(id), SUCCESS_MS);
		return () => clock.clearTimeout(handle);
	}, [id, kind, clock, onDismiss]);

	const action =
		toast.action ??
		(kind === "error" ? { label: "Dismiss", onAction: () => undefined } : null);

	return (
		<div
			role={kind === "success" ? "status" : "alert"}
			className={[styles.toast, styles[kind]].join(" ")}
		>
			<span className={styles.dot} aria-hidden="true" />
			<p className={styles.text}>{kind === "error" ? errorText(text) : text}</p>
			{action && (
				<button
					type="button"
					className={styles.action}
					onClick={() => {
						action.onAction();
						onDismiss(id);
					}}
				>
					{action.label}
				</button>
			)}
		</div>
	);
}
