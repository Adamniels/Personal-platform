import type { ComponentProps, ReactNode } from "react";
import styles from "./status.module.css";

export type ChipProps = Omit<ComponentProps<"span">, "children"> & {
	/** The tool's name, or what else the chip names. */
	children: ReactNode;
};

/** The outlined tool chip, a 22px pill, from "Badge and status label" in
 * section 4 of design/DESIGN.md. Everything a native span takes passes
 * through. */
export function Chip({ className, ...rest }: ChipProps) {
	return (
		<span
			className={[styles.chip, className].filter(Boolean).join(" ")}
			{...rest}
		/>
	);
}
