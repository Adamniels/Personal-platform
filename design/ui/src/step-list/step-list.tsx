import { Check } from "lucide-react";
import {
	Children,
	type ComponentProps,
	createContext,
	isValidElement,
	type ReactNode,
	useContext,
} from "react";
import hidden from "../visually-hidden.module.css";
import styles from "./step-list.module.css";

/** Where a step is: done, the one being worked on, or still to come. */
export type StepState = "done" | "current" | "to-come";

/** A step's number, from one, by its place in the list. */
const NumberContext = createContext(0);

export type StepListProps = ComponentProps<"ol"> & {
	/** The `StepListItem`s, in their order. Each is a direct child, so it
	 * knows its number. */
	children: ReactNode;
};

/** A step list, from "Step list" in section 4 of design/DESIGN.md: an ordered
 * list of steps, each a 32px circle and its content beside it. Everything a
 * native ol takes passes through, so give it an `aria-label` where the page
 * has no heading for it. */
export function StepList({ className, children, ...rest }: StepListProps) {
	const steps = Children.toArray(children);
	return (
		<ol
			className={[styles.list, className].filter(Boolean).join(" ")}
			{...rest}
		>
			{steps.map((step, index) => (
				<NumberContext.Provider
					// toArray gives every element a key, so its own is kept.
					key={isValidElement(step) && step.key !== null ? step.key : index}
					value={index + 1}
				>
					{step}
				</NumberContext.Provider>
			))}
		</ol>
	);
}

export type StepListItemProps = Omit<
	ComponentProps<"li">,
	"children" | "title"
> & {
	state: StepState;
	/** The step's title. */
	title: ReactNode;
	/** A status label beside the title, such as "Needs you" on the current
	 * step when it is the user's. */
	aside?: ReactNode;
	/** What the step holds: one line of what happened when it is done, its
	 * content open when it is current, one line of what comes there when it
	 * is still to come. */
	children?: ReactNode;
};

/** The words that say a step's state to assistive technology, which hears
 * the current one as the list's current step instead. */
const SAID: Record<StepState, string | null> = {
	done: "Done: ",
	current: null,
	"to-come": "To come: ",
};

/** One step of a `StepList`: its circle, with a check when it is done and
 * its number otherwise, drawn, so hidden from assistive technology, which
 * hears the state in words and the number from the list; then its title, its
 * aside and its content. */
export function StepListItem({
	state,
	title,
	aside,
	className,
	children,
	...rest
}: StepListItemProps) {
	const number = useContext(NumberContext);
	const said = SAID[state];
	return (
		<li
			className={[styles.step, className].filter(Boolean).join(" ")}
			data-state={state}
			aria-current={state === "current" ? "step" : undefined}
			{...rest}
		>
			{said === null ? null : <span className={hidden.hidden}>{said}</span>}
			<span className={styles.mark} aria-hidden="true">
				{state === "done" ? (
					<Check className={styles.check} size={16} strokeWidth={2.5} />
				) : (
					number
				)}
			</span>
			<div className={styles.body}>
				<div className={styles.head}>
					<span className={styles.title}>{title}</span>
					{aside}
				</div>
				{children === undefined ? null : (
					<div className={styles.content}>{children}</div>
				)}
			</div>
		</li>
	);
}
