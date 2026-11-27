// A project's tokens per day on "Usage and history": one bar a day for the
// last 30 days ending today, as the service counts them in Dev flow's time
// zone, whatever the period chosen. Plain SVG in the platform's colour tokens,
// no chart library: the highest day's figure at the top and the first and last
// dates under the bars. The drawing is an image whose label sums the chart up
// for screen readers; over it lies a range input, the real control that
// chooses a day, so a day's date and tokens show while its bar is pointed at,
// or chosen with the arrow keys, Home and End, or a tap on a phone. See "Usage
// and history" in devflow/README.md.
import { type MouseEvent, useState } from "react";
import type { GetAnswer } from "./api/client";
import styles from "./tokens-per-day.module.css";

type UsageDay = GetAnswer<"/usage">["projects"][number]["days"][number];

const COUNT = new Intl.NumberFormat("en-GB");

// In UTC, so the date the service counted is the date shown, wherever the
// browser is.
const DAY = new Intl.DateTimeFormat("en-GB", {
	day: "numeric",
	month: "short",
	timeZone: "UTC",
});

/** A day as the service names it, "2026-11-27", as the page says it, "27
 * Nov". */
export function dayLabel(date: string): string {
	return DAY.format(new Date(`${date}T00:00:00Z`));
}

/** A day and its tokens, "26 Nov: 1,000 tokens". */
function dayAndTokens(day: UsageDay): string {
	return `${dayLabel(day.date)}: ${COUNT.format(day.tokens)} tokens`;
}

/** The width of a day's slot, its bar's and the height of the drawing, in
 * the drawing's own units; the drawing is stretched to the chart's width. */
const SLOT = 10;
const BAR = 7;
const HEIGHT = 100;
/** The least a day with any tokens is drawn, so it never looks like none. */
const LEAST = 2;

export function TokensPerDay({
	project,
	days,
}: {
	project: string;
	days: readonly UsageDay[];
}) {
	const last = days.length - 1;
	// The day the pointer is over, and the day the range input holds, which
	// shows while it has focus; the pointer's wins.
	const [pointed, setPointed] = useState<number | null>(null);
	const [chosen, setChosen] = useState(last);
	const [focused, setFocused] = useState(false);
	const active = pointed ?? (focused ? chosen : null);
	const shown = active === null ? undefined : days[active];

	// The highest day, the latest of any that tie.
	const most = days.reduce<UsageDay | undefined>(
		(best, day) =>
			best === undefined || day.tokens >= best.tokens ? day : best,
		undefined,
	);
	const top = most?.tokens ?? 0;
	const span = `${project}: tokens per day, the last ${days.length} days`;
	const label =
		most === undefined || top === 0
			? `${span}, none`
			: `${span}, most on ${dayLabel(most.date)}, ${COUNT.format(top)}`;
	const first = days[0];
	const end = days.at(-1);

	/** The day under the pointer, by where it is across the bars. */
	const point = (event: MouseEvent<HTMLInputElement>) => {
		const box = event.currentTarget.getBoundingClientRect();
		if (box.width <= 0 || days.length === 0) return;
		const at = Math.floor(
			((event.clientX - box.left) / box.width) * days.length,
		);
		setPointed(Math.min(last, Math.max(0, at)));
	};

	return (
		<figure className={styles.chart}>
			<div className={styles.top}>
				<span className={styles.title}>Tokens per day</span>
				{/* What the range input says as its value, shown; hidden here so it
				 * is not read twice. */}
				<span className={styles.shown} aria-hidden="true">
					{shown === undefined ? "" : dayAndTokens(shown)}
				</span>
			</div>
			<span className={styles.most} aria-hidden="true">
				{COUNT.format(top)}
			</span>
			<div className={styles.plot}>
				<svg
					className={styles.bars}
					role="img"
					aria-label={label}
					viewBox={`0 0 ${days.length * SLOT} ${HEIGHT}`}
					preserveAspectRatio="none"
				>
					{days.map((day, index) => {
						const height =
							day.tokens === 0
								? 0
								: Math.max(LEAST, (day.tokens / top) * HEIGHT);
						return (
							<g
								key={day.date}
								className={styles.slot}
								data-day={day.date}
								data-active={index === active}
							>
								<rect
									className={styles.column}
									x={index * SLOT}
									y={0}
									width={SLOT}
									height={HEIGHT}
								/>
								<rect
									className={styles.bar}
									data-bar=""
									x={index * SLOT + (SLOT - BAR) / 2}
									y={HEIGHT - height}
									width={BAR}
									height={height}
								/>
							</g>
						);
					})}
				</svg>
				{days.length > 0 && (
					<input
						type="range"
						className={styles.days}
						aria-label={`A day of ${project}'s tokens`}
						aria-valuetext={
							days[chosen] === undefined
								? undefined
								: dayAndTokens(days[chosen])
						}
						min={0}
						max={last}
						step={1}
						value={chosen}
						onChange={(event) => setChosen(Number(event.target.value))}
						onFocus={() => setFocused(true)}
						onBlur={() => setFocused(false)}
						onMouseMove={point}
						onMouseLeave={() => setPointed(null)}
					/>
				)}
			</div>
			<div className={styles.dates} aria-hidden="true">
				<span>{first === undefined ? "" : dayLabel(first.date)}</span>
				<span>{end === undefined ? "" : dayLabel(end.date)}</span>
			</div>
		</figure>
	);
}
