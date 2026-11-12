import type { ComponentProps } from "react";
import styles from "./button.module.css";

export type ButtonKind = "primary" | "secondary" | "ghost" | "destructive";

export type ButtonProps = ComponentProps<"button"> & {
	/** How much the button stands out. At most one primary per view. */
	kind?: ButtonKind;
};

/** A native button in one of the four kinds of "Button" in section 4 of
 * design/DESIGN.md. Everything a native button takes passes through, ref
 * included; type defaults to "button" so it never submits a form unasked. */
export function Button({
	kind = "secondary",
	type = "button",
	className,
	...rest
}: ButtonProps) {
	const classes = [styles.button, styles[kind], className]
		.filter(Boolean)
		.join(" ");
	return <button type={type} className={classes} {...rest} />;
}
