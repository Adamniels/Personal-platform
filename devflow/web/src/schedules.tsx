// The card "When it runs" on a project's settings page: whether the project runs always or by
// schedule, its windows, each with its days and times, a switch for on and off, Edit and Remove,
// "Add a schedule", and the user's time zone, which every window is wall clock in, with Change.
// Every change saves at once through the schedules' and the time zone's own routes, never the
// settings page's Save, and a refusal shows under its field. And the card "When it runs" on the
// project's page: always, or each window with its switch and when it runs next. See "The pages"
// and "Schedules" in devflow/README.md.
import { Button, Card, Select, Switch, TextField } from "@platform/ui";
import { type FormEvent, useCallback, useEffect, useId, useState } from "react";
import { describe } from "./actions";
import { navigate, plainClick } from "./address";
import { ApiError, type GetAnswer } from "./api/client";
import styles from "./schedules.module.css";
import { settingsPath } from "./sections";

export type ProjectSchedules = GetAnswer<"/projects/{id}/schedules">;
export type Schedule = ProjectSchedules["schedules"][number];

/** A window as saving takes it: one without an id is new. */
export type ScheduleWindow = Omit<Schedule, "id"> & { id: string | null };

/** What saving takes: the mode and the whole list. */
export type SchedulesRequest = {
	runs_by: string;
	schedules: ScheduleWindow[];
};

/** How the card reads and saves a project's schedules and the user's time
 * zone, Dev flow's API in the page and a test's own in a test. */
export type SchedulesApi = {
	loadSchedules: (id: string) => Promise<ProjectSchedules>;
	/** Replaces the mode and the list, and answers what Dev flow left. */
	saveSchedules: (
		id: string,
		request: SchedulesRequest,
	) => Promise<ProjectSchedules>;
	/** Sets the user's zone, for every project, and answers it as saved. */
	saveTimeZone: (zone: string) => Promise<string>;
};

export const HELP =
	"Outside every schedule that is on, the run under way finishes and nothing new starts.";
export const SAVED = "Saved.";
export const NOT_READ = "The schedules could not be read. Try again.";

/** The days, in the week's order, with their short names. */
export const DAYS: { value: string; name: string }[] = [
	{ value: "mon", name: "Mon" },
	{ value: "tue", name: "Tue" },
	{ value: "wed", name: "Wed" },
	{ value: "thu", name: "Thu" },
	{ value: "fri", name: "Fri" },
	{ value: "sat", name: "Sat" },
	{ value: "sun", name: "Sun" },
];

/** A window's days in short: "Every day", or the short names, three or more
 * days in a row as a range, such as "Mon–Fri" or "Mon, Wed–Fri, Sun". */
export function daysLine(days: readonly string[]): string {
	const names = DAYS.map((day) => (days.includes(day.value) ? day.name : null));
	if (names.every((name) => name !== null)) return "Every day";
	const parts: string[] = [];
	let run: string[] = [];
	const close = () => {
		if (run.length >= 3) parts.push(`${run[0]}–${run[run.length - 1]}`);
		else parts.push(...run);
		run = [];
	};
	for (const name of names) {
		if (name === null) close();
		else run.push(name);
	}
	close();
	return parts.join(", ");
}

/** A window's line under its name: its days and its times. */
export const windowLine = (entry: Pick<Schedule, "days" | "from" | "to">) =>
	`${daysLine(entry.days)} · ${entry.from}–${entry.to}`;

/** The settings page's card, where "Edit schedules" goes. */
export const SCHEDULES_ANCHOR = "settings-schedules";

/** Where "Edit schedules" goes: the settings page, at its card. */
export const schedulesPath = (id: string) =>
	`${settingsPath(id)}#${SCHEDULES_ANCHOR}`;

/** An instant as wall clock in the user's zone: its short day and its time,
 * "Fri 22:00", as Dev flow's own sentences say it. */
export const dayTime = (at: string, zone: string) =>
	new Intl.DateTimeFormat("en-GB", {
		timeZone: zone,
		weekday: "short",
		hour: "2-digit",
		minute: "2-digit",
		hourCycle: "h23",
	}).format(new Date(at));

/** An instant's time alone, in the user's zone. */
const timeOf = (at: string, zone: string) =>
	new Intl.DateTimeFormat("en-GB", {
		timeZone: zone,
		hour: "2-digit",
		minute: "2-digit",
		hourCycle: "h23",
	}).format(new Date(at));

/** What the project page's card says of when it runs: always; running now,
 * until when; when it runs next; or that nothing runs. */
export function whenLine(schedules: ProjectSchedules): string {
	if (schedules.runs_by !== "schedule") return "Always";
	if (schedules.running_until !== null)
		return `Running now, until ${timeOf(schedules.running_until, schedules.time_zone)}`;
	if (schedules.next_start !== null)
		return `Runs next ${dayTime(schedules.next_start, schedules.time_zone)}`;
	return "No schedule is on, so nothing runs";
}

/** "Edit schedules": a link to the settings page's card, opened in place on
 * a plain click, drawn as a secondary button. */
export function EditSchedules({ id }: { id: string }) {
	const to = schedulesPath(id);
	return (
		<a
			className={styles.editSchedules}
			href={to}
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					navigate(to);
				}
			}}
		>
			Edit schedules
		</a>
	);
}

/** The card "When it runs" in the project page's side column: always, or
 * each window with its switch, saved at once, and when it runs next or
 * until when; with "Edit schedules". Nothing before the schedules answer. */
export function WhenItRunsCard({
	id,
	loadSchedules,
	saveSchedules,
	className,
}: Pick<SchedulesApi, "loadSchedules" | "saveSchedules"> & {
	id: string;
	className?: string;
}) {
	const [schedules, setSchedules] = useState<ProjectSchedules | null>();
	const [busy, setBusy] = useState(false);
	const [failed, setFailed] = useState<string | null>(null);

	useEffect(() => {
		let current = true;
		loadSchedules(id).then(
			(answer) => {
				if (current) setSchedules(answer);
			},
			() => {
				if (current) setSchedules(null);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadSchedules]);

	if (schedules === undefined) return null;
	const list = schedules?.schedules.map(toRequest) ?? [];

	/** Switches a window, which moves at once and goes back if Dev flow
	 * refuses it. */
	const toggle = async (place: number, active: boolean) => {
		if (!schedules) return;
		const before = schedules;
		const next = before.schedules.map((entry, at) =>
			at === place ? { ...entry, active } : entry,
		);
		setBusy(true);
		setFailed(null);
		setSchedules({ ...before, schedules: next });
		try {
			setSchedules(
				await saveSchedules(id, {
					runs_by: before.runs_by,
					schedules: next.map(toRequest),
				}),
			);
		} catch (error) {
			setSchedules(before);
			setFailed(`Not saved: ${describe(error)}`);
		} finally {
			setBusy(false);
		}
	};

	return (
		<Card
			title="When it runs"
			className={[styles.card, className].filter(Boolean).join(" ")}
			footer={<EditSchedules id={id} />}
		>
			{schedules === null ? (
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			) : (
				<div className={styles.group}>
					<p className={styles.when}>{whenLine(schedules)}</p>
					{schedules.runs_by !== "schedule" || list.length === 0 ? null : (
						<ul className={styles.windows} aria-label="Schedules">
							{list.map((entry, place) => (
								<li key={entry.id} className={styles.window}>
									<Switch
										className={styles.switch}
										label={entry.name}
										help={windowLine(entry)}
										checked={entry.active}
										disabled={busy}
										onChange={(event) => toggle(place, event.target.checked)}
									/>
								</li>
							))}
						</ul>
					)}
					{failed === null ? null : (
						<p className={styles.error} role="alert">
							{failed}
						</p>
					)}
				</div>
			)}
		</Card>
	);
}

/** The zones the select offers: the browser's IANA zones, with UTC and the
 * one saved, whichever the browser does not list. */
function zones(saved: string): string[] {
	const listed =
		typeof Intl.supportedValuesOf === "function"
			? Intl.supportedValuesOf("timeZone")
			: [];
	return [...new Set([saved, "UTC", ...listed])].sort((a, b) =>
		a.localeCompare(b),
	);
}

/** What a refusal says of each field, by its name on the wire. */
function refusals(error: unknown): Record<string, string> | undefined {
	if (!(error instanceof ApiError) || error.problem.status !== 422)
		return undefined;
	return Object.fromEntries(
		Object.entries(error.problem.errors ?? {}).map(([field, messages]) => [
			field,
			messages[0] ?? "is not valid",
		]),
	);
}

const toRequest = (schedule: Schedule): ScheduleWindow => ({
	id: schedule.id,
	name: schedule.name,
	days: schedule.days,
	from: schedule.from,
	to: schedule.to,
	active: schedule.active,
});

/** The window being written, the place it is saved at in the list, a new
 * one's past its end. */
type Draft = { place: number; entry: ScheduleWindow; title: string };

/** The card "When it runs", with the anchor the settings page's links and
 * "Edit schedules" go to. Nothing before the schedules answer. */
export function SchedulesCard({
	id,
	anchor,
	loadSchedules,
	saveSchedules,
	saveTimeZone,
}: SchedulesApi & { id: string; anchor: string }) {
	const [schedules, setSchedules] = useState<ProjectSchedules | null>();
	const [saved, setSaved] = useState(false);
	const [failed, setFailed] = useState<string | null>(null);
	const [busy, setBusy] = useState(false);
	const [draft, setDraft] = useState<Draft | null>(null);
	const [invalid, setInvalid] = useState<Record<string, string>>({});
	const [changingZone, setChangingZone] = useState(false);
	const [zoneError, setZoneError] = useState<string | undefined>();
	const modeLabel = useId();

	useEffect(() => {
		let current = true;
		loadSchedules(id).then(
			(answer) => {
				if (current) setSchedules(answer);
			},
			() => {
				if (current) setSchedules(null);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadSchedules]);

	// Opened by "Edit schedules" from another page, the card scrolls into view
	// once it is there, as a link within the page would.
	const shown = schedules !== undefined;
	useEffect(() => {
		if (shown && window.location.hash === `#${anchor}`)
			document.getElementById(anchor)?.scrollIntoView?.();
	}, [shown, anchor]);

	/** Saves the mode and the list, and answers whether Dev flow took them;
	 * a refusal is kept by field for the editor, anything else said above. */
	const save = useCallback(
		async (request: SchedulesRequest): Promise<boolean> => {
			setBusy(true);
			setSaved(false);
			setFailed(null);
			// The mode and a switch move as they are clicked, and go back if
			// Dev flow refuses; a window still being added waits for its id.
			const before = schedules;
			if (before && request.schedules.every((entry) => entry.id !== null))
				setSchedules({
					...before,
					runs_by: request.runs_by,
					schedules: request.schedules as Schedule[],
				});
			try {
				setSchedules(await saveSchedules(id, request));
				setInvalid({});
				setSaved(true);
				return true;
			} catch (error) {
				setSchedules(before);
				const fields = refusals(error);
				setInvalid(fields ?? {});
				setFailed(
					fields === undefined
						? `The schedules were not saved: ${describe(error)}`
						: null,
				);
				return false;
			} finally {
				setBusy(false);
			}
		},
		[id, saveSchedules, schedules],
	);

	if (schedules === undefined) return null;
	if (schedules === null)
		return (
			<Card title="When it runs" id={anchor} className={styles.card}>
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			</Card>
		);

	const list = schedules.schedules.map(toRequest);
	const bySchedule = schedules.runs_by === "schedule";
	const replace = (place: number, entry: ScheduleWindow | null) => {
		const next = [...list];
		if (entry === null) next.splice(place, 1);
		else next[place] = entry;
		return save({ runs_by: schedules.runs_by, schedules: next });
	};

	const changeZone = async (zone: string) => {
		setSaved(false);
		try {
			await saveTimeZone(zone);
			setZoneError(undefined);
			// The next start moves with the zone, so the card reads it again.
			setSchedules(await loadSchedules(id));
			setChangingZone(false);
			setSaved(true);
		} catch (error) {
			setZoneError(
				refusals(error)?.time_zone ?? `Not changed: ${describe(error)}`,
			);
		}
	};

	return (
		<Card title="When it runs" id={anchor} className={styles.card}>
			<div className={styles.group}>
				<div
					role="radiogroup"
					aria-labelledby={modeLabel}
					className={styles.track}
				>
					<span id={modeLabel} className={styles.hidden}>
						How it runs
					</span>
					{[
						{ value: "always", name: "Always" },
						{ value: "schedule", name: "By schedule" },
					].map(({ value, name }) => (
						<label key={value} className={styles.segment}>
							<input
								type="radio"
								name={`${anchor}-mode`}
								value={value}
								className={styles.radio}
								checked={schedules.runs_by === value}
								disabled={busy}
								onChange={() => save({ runs_by: value, schedules: list })}
							/>
							{name}
						</label>
					))}
				</div>
				<p className={styles.hint}>{HELP}</p>
				{bySchedule ? (
					<>
						{list.length === 0 ? null : (
							<ul className={styles.windows} aria-label="Schedules">
								{list.map((entry, place) =>
									draft?.place === place ? null : (
										<li key={entry.id} className={styles.entry}>
											<Switch
												className={styles.switch}
												label={entry.name}
												help={windowLine(entry)}
												checked={entry.active}
												disabled={busy}
												onChange={(event) =>
													replace(place, {
														...entry,
														active: event.target.checked,
													})
												}
											/>
											<div className={styles.actions}>
												<Button
													kind="ghost"
													aria-label={`Edit ${entry.name}`}
													disabled={busy}
													onClick={() => {
														setInvalid({});
														setDraft({
															place,
															entry,
															title: `Edit ${entry.name}`,
														});
													}}
												>
													Edit
												</Button>
												<Button
													kind="ghost"
													className={styles.remove}
													aria-label={`Remove ${entry.name}`}
													disabled={busy}
													onClick={() => replace(place, null)}
												>
													Remove
												</Button>
											</div>
										</li>
									),
								)}
							</ul>
						)}
						{draft === null ? (
							list.length >= 20 ? null : (
								<div>
									<Button
										kind="secondary"
										disabled={busy}
										onClick={() => {
											setInvalid({});
											setDraft({
												place: list.length,
												title: "A new schedule",
												entry: {
													id: null,
													name: "",
													days: [],
													from: "09:00",
													to: "17:00",
													active: true,
												},
											});
										}}
									>
										Add a schedule
									</Button>
								</div>
							)
						) : (
							<WindowEditor
								draft={draft}
								busy={busy}
								invalid={invalid}
								onCancel={() => {
									setDraft(null);
									setInvalid({});
								}}
								onSave={async (entry) => {
									if (await replace(draft.place, entry)) setDraft(null);
								}}
							/>
						)}
					</>
				) : null}
				<div className={styles.zone}>
					{changingZone ? (
						<Select
							label="Time zone"
							help="Used by every project"
							value={schedules.time_zone}
							error={zoneError}
							onChange={(event) => changeZone(event.target.value)}
						>
							{zones(schedules.time_zone).map((zone) => (
								<option key={zone} value={zone}>
									{zone}
								</option>
							))}
						</Select>
					) : (
						<p className={styles.zoneLine}>
							<span>Times are in {schedules.time_zone}</span>
							<Button kind="ghost" onClick={() => setChangingZone(true)}>
								Change
							</Button>
						</p>
					)}
				</div>
				{failed === null ? null : (
					<p className={styles.error} role="alert">
						{failed}
					</p>
				)}
				{saved ? <output className={styles.saved}>{SAVED}</output> : null}
			</div>
		</Card>
	);
}

/** The editor of one window: its name, seven day toggles and its two times,
 * each refusal under its field, a field's place in the list being the
 * draft's. */
function WindowEditor({
	draft,
	busy,
	invalid,
	onCancel,
	onSave,
}: {
	draft: Draft;
	busy: boolean;
	invalid: Record<string, string>;
	onCancel: () => void;
	onSave: (entry: ScheduleWindow) => Promise<void>;
}) {
	const [entry, setEntry] = useState(draft.entry);
	const daysLabel = useId();
	const field = (name: string) => invalid[`schedules[${draft.place}].${name}`];
	const others = Object.entries(invalid)
		.filter(([name]) => !name.startsWith(`schedules[${draft.place}].`))
		.map(([name, message]) => `${name} ${message}`);
	const isNew = draft.entry.id === null;

	const submit = (event: FormEvent) => {
		event.preventDefault();
		void onSave(entry);
	};

	return (
		<form
			className={styles.editor}
			aria-label={draft.title}
			onSubmit={submit}
			noValidate
		>
			<TextField
				label="Name"
				autoComplete="off"
				maxLength={60}
				value={entry.name}
				error={field("name")}
				onChange={(event) => setEntry({ ...entry, name: event.target.value })}
			/>
			<fieldset className={styles.days} aria-labelledby={daysLabel}>
				<legend id={daysLabel} className={styles.label}>
					Days
				</legend>
				<div className={styles.dayTrack}>
					{DAYS.map((day) => (
						<label key={day.value} className={styles.day}>
							<input
								type="checkbox"
								className={styles.radio}
								checked={entry.days.includes(day.value)}
								onChange={(event) =>
									setEntry({
										...entry,
										days: DAYS.map((each) => each.value).filter((each) =>
											each === day.value
												? event.target.checked
												: entry.days.includes(each),
										),
									})
								}
							/>
							{day.name}
						</label>
					))}
				</div>
				{field("days") === undefined ? null : (
					<p className={styles.fieldError} role="alert">
						{field("days")}
					</p>
				)}
			</fieldset>
			<div className={styles.times}>
				<TextField
					label="From"
					type="time"
					step={60}
					className={styles.mono}
					value={entry.from}
					error={field("from")}
					onChange={(event) => setEntry({ ...entry, from: event.target.value })}
				/>
				<TextField
					label="To"
					type="time"
					step={60}
					className={styles.mono}
					value={entry.to}
					error={field("to")}
					onChange={(event) => setEntry({ ...entry, to: event.target.value })}
				/>
			</div>
			<p className={styles.hint}>
				A entry whose To is earlier than its From ends the next day.
			</p>
			{others.length === 0 ? null : (
				<p className={styles.error} role="alert">
					{others.join("; ")}
				</p>
			)}
			<div className={styles.editorActions}>
				<Button kind="primary" type="submit" disabled={busy}>
					{isNew ? "Add" : "Save"}
				</Button>
				<Button kind="ghost" disabled={busy} onClick={onCancel}>
					Cancel
				</Button>
			</div>
		</form>
	);
}
