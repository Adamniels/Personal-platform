// The "Usage and history" tab: what each project, feature and card used over the last 7 days, the
// last 30 or all time, the period in the address. A table per project, most tokens first, each
// opening into its features and its cards on their own, and a feature into its cards and its own
// runs; a project's own runs, its checks and setup, are "Setting up". Under each table, the
// project's tokens per day over the last 30 days, whatever the period. Tokens by the one definition
// the service sends, cache reads beside them; tokens and time, never money. The figures come from
// GET /devflow/api/usage. See "The pages" in devflow/README.md.
import { EmptyState, Select, Table, type TableColumn } from "@platform/ui";
import { useQuery } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { navigate, plainClick, useSearch } from "./address";
import type { GetAnswer } from "./api/client";
import { cardPath, featurePath } from "./sections";
import { dayLabel, TokensPerDay } from "./tokens-per-day";
import styles from "./usage.module.css";

export type UsageView = GetAnswer<"/usage">;
type UsageProject = UsageView["projects"][number];
type UsageFeature = UsageProject["features"][number];
type UsageCard = UsageProject["cards"][number];

/** How the tab reads what was used in a period, Dev flow's API in the page
 * and a test's own in a test. */
export type UsageApi = { loadUsage: (period: Period) => Promise<UsageView> };

/** The periods, as the address and the API name them, and as the picker
 * says them. */
export const PERIODS = [
	{
		value: "7d",
		label: "Last 7 days",
		nothing: "Nothing used in the last 7 days",
	},
	{
		value: "30d",
		label: "Last 30 days",
		nothing: "Nothing used in the last 30 days",
	},
	{ value: "all", label: "All time", nothing: "Nothing used yet" },
] as const;

export type Period = (typeof PERIODS)[number]["value"];

/** The period without one in the address, or with one it does not know. */
const DEFAULT: Period = "30d";

/** The tab's address with a period. */
export function usagePath(period: Period): string {
	return `/devflow/usage?period=${period}`;
}

/** The period the address names, 30 days when it names none it knows. */
function periodIn(search: string): Period {
	const asked = new URLSearchParams(search).get("period");
	return PERIODS.find((period) => period.value === asked)?.value ?? DEFAULT;
}

const COUNT = new Intl.NumberFormat("en-GB");

/** A span of time in its two largest units: "42 s", "12 min", "1 h 42 min",
 * "40 d 0 h". */
export function duration(ms: number): string {
	const seconds = Math.max(0, Math.floor(ms / 1000));
	const days = Math.floor(seconds / 86_400);
	const hours = Math.floor((seconds % 86_400) / 3600);
	const minutes = Math.floor((seconds % 3600) / 60);
	if (days > 0) return `${days} d ${hours} h`;
	if (hours > 0) return `${hours} h ${minutes} min`;
	if (minutes > 0) return `${minutes} min`;
	return `${seconds} s`;
}

export function Usage({ loadUsage }: UsageApi) {
	const period = periodIn(useSearch());
	const read = useQuery({
		queryKey: ["devflow", "usage", period],
		queryFn: () => loadUsage(period),
		refetchOnWindowFocus: true,
	});
	const nothing = PERIODS.find((each) => each.value === period)?.nothing;

	return (
		<section className={styles.usage} aria-label="Usage and history">
			<div className={styles.bar}>
				<Select
					label="Period"
					className={styles.period}
					value={period}
					onChange={(event) =>
						navigate(usagePath(event.target.value as Period))
					}
				>
					{PERIODS.map((each) => (
						<option key={each.value} value={each.value}>
							{each.label}
						</option>
					))}
				</Select>
			</div>
			{read.isError ? (
				<p className={styles.error} role="alert">
					Usage could not be read. Try again.
				</p>
			) : read.data === undefined ? null : read.data.projects.length === 0 ? (
				<EmptyState
					title={nothing ?? ""}
					detail="What each project, feature and card uses shows here once a worker has run a job."
				/>
			) : (
				read.data.projects.map((project) => (
					<ProjectTable key={project.id} project={project} />
				))
			)}
		</section>
	);
}

/** One row of a project's table: the project, a feature, a card, a
 * feature's own runs or Setting up, with the figures it has. */
type Row = {
	key: string;
	/** How far it sits in: the project 0, its features, cards and Setting up
	 * 1, a feature's cards and own runs 2. */
	depth: 0 | 1 | 2;
	name: ReactNode;
	runs: number;
	agent_ms: number;
	/** A card's or a feature's whole, null for any other row. */
	elapsed_ms: number | null;
	tokens: number;
	cache_reads: number;
	fix_rounds: number | null;
	sent_back: number | null;
};

const blank = (value: number | null, show: (value: number) => string) =>
	value === null ? "" : show(value);

const COLUMNS: readonly TableColumn<Row>[] = [
	{
		key: "name",
		header: "Name",
		cell: (row) => (
			<div className={styles.name} data-depth={row.depth}>
				{row.name}
			</div>
		),
	},
	{
		key: "runs",
		header: "Runs",
		numeric: true,
		priority: 2,
		cell: (row) => COUNT.format(row.runs),
		fold: (row) =>
			`${COUNT.format(row.runs)} ${row.runs === 1 ? "run" : "runs"}`,
	},
	{
		key: "agent",
		header: "Agent time",
		numeric: true,
		priority: 2,
		cell: (row) => duration(row.agent_ms),
		fold: (row) => `${duration(row.agent_ms)} agent`,
	},
	{
		key: "elapsed",
		header: "Elapsed",
		numeric: true,
		priority: 2,
		cell: (row) => blank(row.elapsed_ms, duration),
		fold: (row) => blank(row.elapsed_ms, (ms) => `${duration(ms)} elapsed`),
	},
	{
		key: "tokens",
		header: "Tokens",
		numeric: true,
		priority: 2,
		cell: (row) => COUNT.format(row.tokens),
		fold: (row) => `${COUNT.format(row.tokens)} tokens`,
	},
	{
		key: "cache",
		header: "Cache reads",
		numeric: true,
		priority: 2,
		cell: (row) => COUNT.format(row.cache_reads),
		fold: (row) => `${COUNT.format(row.cache_reads)} cache reads`,
	},
	{
		key: "fix",
		header: "Fix rounds",
		numeric: true,
		priority: 2,
		cell: (row) => blank(row.fix_rounds, (n) => COUNT.format(n)),
		fold: (row) =>
			blank(
				row.fix_rounds,
				(n) => `${n} ${n === 1 ? "fix round" : "fix rounds"}`,
			),
	},
	{
		key: "sent",
		header: "Sent back",
		numeric: true,
		priority: 2,
		cell: (row) => blank(row.sent_back, (n) => COUNT.format(n)),
		fold: (row) => blank(row.sent_back, (n) => `sent back ${n}`),
	},
];

/** A project's table: its own row, closed; open, its features, each of which
 * opens into its cards and its own runs, its cards on their own and Setting
 * up. Under it, its tokens per day. */
function ProjectTable({ project }: { project: UsageProject }) {
	const [open, setOpen] = useState(false);
	const [features, setFeatures] = useState<ReadonlySet<number>>(new Set());
	const toggle = (number: number) =>
		setFeatures((shown) => {
			const next = new Set(shown);
			if (!next.delete(number)) next.add(number);
			return next;
		});

	const rows: Row[] = [
		{
			...project,
			key: "project",
			depth: 0,
			name: (
				<Toggle
					open={open}
					onToggle={() => setOpen((was) => !was)}
					label={project.name}
				>
					<span className={styles.project}>{project.name}</span>
				</Toggle>
			),
			elapsed_ms: null,
			fix_rounds: null,
			sent_back: null,
		},
	];
	if (open) {
		for (const feature of project.features) {
			const shown = features.has(feature.number);
			rows.push(
				featureRow(project.id, feature, shown, () => toggle(feature.number)),
			);
			if (!shown) continue;
			for (const card of feature.cards) rows.push(cardRow(project.id, card, 2));
			if (feature.own !== null) {
				rows.push({
					...feature.own,
					key: `own-${feature.number}`,
					depth: 2,
					name: <span>The feature's own runs</span>,
					elapsed_ms: null,
					fix_rounds: null,
					sent_back: null,
				});
			}
		}
		for (const card of project.cards) rows.push(cardRow(project.id, card, 1));
		if (project.setting_up !== null) {
			rows.push({
				...project.setting_up,
				key: "setting-up",
				depth: 1,
				name: <span>Setting up</span>,
				elapsed_ms: null,
				fix_rounds: null,
				sent_back: null,
			});
		}
	}

	return (
		<div className={styles.project}>
			<Table
				className={styles.table}
				label={`Usage of ${project.name}`}
				columns={COLUMNS}
				rows={rows}
				rowKey={(row) => row.key}
			/>
			<TokensPerDay project={project.name} days={project.days} />
			<KeySpend project={project.name} days={project.days} />
		</div>
	);
}

type UsageDay = UsageProject["days"][number];

/** What the API key cost on a day, as the page says it, "API key: 3.20 USD". */
export const keySpend = (usd: number) => `API key: ${usd.toFixed(2)} USD`;

/** Each day of the last 30 the project ran on the API key, with what the key
 * cost that day by Claude Code's estimate, oldest first; nothing for a
 * project that never ran on it. The one place Dev flow shows money, since
 * the key's spend is real. */
function KeySpend({
	project,
	days,
}: {
	project: string;
	days: readonly UsageDay[];
}) {
	const spent = days.filter(
		(day): day is UsageDay & { api_key_usd: number } =>
			day.api_key_usd !== null && day.api_key_usd !== undefined,
	);
	if (spent.length === 0) return null;
	return (
		<ul
			className={styles.spend}
			aria-label={`${project}: API key spend per day`}
		>
			{spent.map((day) => (
				<li key={day.date} className={styles.spendDay}>
					<span>{dayLabel(day.date)}</span>
					<span className={styles.amount}>{keySpend(day.api_key_usd)}</span>
				</li>
			))}
		</ul>
	);
}

function featureRow(
	project: string,
	feature: UsageFeature,
	open: boolean,
	onToggle: () => void,
): Row {
	const label = `#${feature.number} ${feature.title}`;
	return {
		...feature,
		key: `feature-${feature.number}`,
		depth: 1,
		name: (
			<span className={styles.feature}>
				<Toggle
					open={open}
					onToggle={onToggle}
					label={`${open ? "Close" : "Open"} ${label}`}
				/>
				<span className={styles.number}>#{feature.number}</span>{" "}
				<PageLink path={featurePath(project, feature.number)}>
					{feature.title}
				</PageLink>
			</span>
		),
	};
}

function cardRow(project: string, card: UsageCard, depth: 1 | 2): Row {
	return {
		...card,
		key: `card-${card.number}`,
		depth,
		name: (
			<span>
				<span className={styles.number}>#{card.number}</span>
				{card.title === null ? null : (
					<>
						{" "}
						<PageLink path={cardPath(project, card.number)}>
							{card.title}
						</PageLink>
					</>
				)}
			</span>
		),
	};
}

/** The button that opens a project or a feature: with `children`, a button
 * holding them and the chevron; without, the chevron alone, named by
 * `label`. */
function Toggle({
	open,
	onToggle,
	label,
	children,
}: {
	open: boolean;
	onToggle: () => void;
	label: string;
	children?: ReactNode;
}) {
	return (
		<button
			type="button"
			className={children ? styles.toggle : styles.chevronOnly}
			aria-expanded={open}
			aria-label={children ? undefined : label}
			onClick={onToggle}
		>
			<svg
				className={styles.chevron}
				data-open={open}
				viewBox="0 0 24 24"
				width="16"
				height="16"
				aria-hidden="true"
			>
				<path d="m9 18 6-6-6-6" />
			</svg>
			{children}
		</button>
	);
}

/** A link to a page of Dev flow's own, followed in place on a plain click. */
function PageLink({ path, children }: { path: string; children: ReactNode }) {
	return (
		<a
			className={styles.link}
			href={path}
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					navigate(path);
				}
			}}
		>
			{children}
		</a>
	);
}
