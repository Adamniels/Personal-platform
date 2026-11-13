// A stand in for the package's toast, "Toast" in section 4 of design/DESIGN.md, until #192 adds
// `ToastProvider` and `useToast` to @platform/ui with the same shape. It holds errors alone, the
// one kind the memory centre raises today. Replace it with the package's when that lands.
import {
	createContext,
	type ReactNode,
	useContext,
	useMemo,
	useRef,
	useState,
} from "react";
import styles from "./toast.module.css";

export type Toasts = {
	/** Shows an error that stays until it is dismissed. */
	error: (text: string) => void;
};

type Shown = { id: number; text: string };

/** At most this many show at once; older ones leave first. */
const MAX_SHOWN = 3;

const ToastContext = createContext<Toasts | null>(null);

export function useToast(): Toasts {
	const toasts = useContext(ToastContext);
	if (!toasts) throw new Error("useToast is used outside ToastProvider");
	return toasts;
}

/** Wraps the page once, and shows its toasts bottom right, above the tab bar
 * on phone. */
export function ToastProvider({ children }: { children: ReactNode }) {
	const [shown, setShown] = useState<Shown[]>([]);
	const nextId = useRef(0);
	const toasts = useMemo<Toasts>(
		() => ({
			error: (text) => {
				const id = nextId.current++;
				setShown((list) => [...list, { id, text }].slice(-MAX_SHOWN));
			},
		}),
		[],
	);

	return (
		<ToastContext.Provider value={toasts}>
			{children}
			<div className={styles.region}>
				{shown.map((toast) => (
					<div key={toast.id} role="alert" className={styles.toast}>
						<span className={styles.dot} aria-hidden="true" />
						<p className={styles.text}>{asError(toast.text)}</p>
						<button
							type="button"
							className={styles.action}
							onClick={() =>
								setShown((list) => list.filter(({ id }) => id !== toast.id))
							}
						>
							Dismiss
						</button>
					</div>
				))}
			</div>
		</ToastContext.Provider>
	);
}

/** An error always starts with the word Error, without doubling it. */
function asError(text: string): string {
	return /^error\b/i.test(text) ? text : `Error: ${text}`;
}
