import { Select, TextField } from "@platform/ui";
import styles from "./history.module.css";
import { MONTHS, type WhenDraft } from "./when";

const PRECISIONS = [
	{ value: "year", label: "Year" },
	{ value: "month", label: "Month" },
	{ value: "day", label: "Day" },
] as const;

/** When something happened: a precision, Year, Month or Day, and the field
 * that matches it, so "sometime in 2021" is said as a year. The error is
 * shown on the field that says the time. */
export function WhenFields({
	legend,
	value,
	error,
	onChange,
}: {
	legend: string;
	value: WhenDraft;
	error?: string;
	onChange: (value: WhenDraft) => void;
}) {
	const year = (
		<TextField
			label="Year"
			inputMode="numeric"
			autoComplete="off"
			maxLength={4}
			value={value.year ?? ""}
			error={value.precision === "day" ? undefined : error}
			onChange={(event) => onChange({ ...value, year: event.target.value })}
		/>
	);
	return (
		<fieldset className={styles.when}>
			<legend className={styles.legend}>{legend}</legend>
			<Select
				label="Precision"
				value={value.precision}
				onChange={(event) =>
					onChange({
						...value,
						precision: event.target.value as WhenDraft["precision"],
					})
				}
			>
				{PRECISIONS.map((precision) => (
					<option key={precision.value} value={precision.value}>
						{precision.label}
					</option>
				))}
			</Select>
			{value.precision === "day" ? (
				<TextField
					label="Day"
					type="date"
					value={value.day ?? ""}
					error={error}
					onChange={(event) => onChange({ ...value, day: event.target.value })}
				/>
			) : null}
			{value.precision === "month" ? (
				<Select
					label="Month"
					value={value.month ?? ""}
					onChange={(event) =>
						onChange({ ...value, month: event.target.value })
					}
				>
					<option value="" disabled>
						Choose a month
					</option>
					{MONTHS.map((month, index) => (
						<option key={month} value={String(index + 1)}>
							{month}
						</option>
					))}
				</Select>
			) : null}
			{value.precision === "day" ? null : year}
		</fieldset>
	);
}
