import type { ComponentProps, ReactNode } from "react";
import { Status, type StatusTone } from "../status/status";
import styles from "./verdict-tile.module.css";

export type VerdictTilesProps = ComponentProps<"ul">;

/** The verdict tiles' grid, `repeat(auto-fit, minmax(180px, 1fr))`, so the
 * tiles share a row on desktop and stack on phone. A list, so give it an
 * `aria-label` where no heading names it; its tiles are its direct children.
 * Everything a native ul takes passes through. */
export function VerdictTiles({ className, ...rest }: VerdictTilesProps) {
	const classes = [styles.tiles, className].filter(Boolean).join(" ");
	return <ul className={classes} {...rest} />;
}

export type VerdictTileProps = Omit<ComponentProps<"li">, "children"> & {
	/** What the tile judges, in caption style, such as "The check". */
	label: string;
	/** The status label's tone. */
	tone: StatusTone;
	/** The status label's word, such as "Passed". */
	status: string;
	/** An optional line of facts under them, in the mono font, such as
	 * "npm run check · exit 0". */
	line?: ReactNode;
};

/** One verdict tile: an inset on --surface-2 with its label, a status
 * label and an optional mono line, read in that order. Everything a native
 * li takes passes through. */
export function VerdictTile({
	label,
	tone,
	status,
	line,
	className,
	...rest
}: VerdictTileProps) {
	const classes = [styles.tile, className].filter(Boolean).join(" ");
	return (
		<li className={classes} {...rest}>
			<span className={styles.label}>{label}</span>
			<Status tone={tone} className={styles.status}>
				{status}
			</Status>
			{line === undefined ? null : <span className={styles.line}>{line}</span>}
		</li>
	);
}
