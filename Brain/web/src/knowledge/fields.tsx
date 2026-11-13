import { Select } from "@platform/ui";
import type { Ref } from "react";
import { FOCUSES, LEVELS, levelMeaning, NOT_SET } from "./levels";

type SettingSelectProps = {
	/** The chosen value, or "" for none. */
	value: string;
	onChange: (value: string) => void;
	/** Whether "Not set" is offered: when adding a topic, or when the topic
	 * has none yet. A value once set is changed, never taken away. */
	unset: boolean;
	ref?: Ref<HTMLSelectElement>;
};

/** The Level select. The native menu holds one line per option, so each
 * option's meaning is the field's second line, its help, for the option
 * chosen. */
export function LevelSelect({
	value,
	onChange,
	unset,
	ref,
}: SettingSelectProps) {
	return (
		<Select
			ref={ref}
			label="Level"
			value={value}
			help={levelMeaning(value)}
			onChange={(event) => onChange(event.target.value)}
		>
			{unset ? <option value="">{NOT_SET}</option> : null}
			{LEVELS.map((level) => (
				<option key={level.value} value={level.value}>
					{level.word}
				</option>
			))}
		</Select>
	);
}

export function FocusSelect({
	value,
	onChange,
	unset,
	ref,
}: SettingSelectProps) {
	return (
		<Select
			ref={ref}
			label="Focus"
			value={value}
			onChange={(event) => onChange(event.target.value)}
		>
			{unset ? <option value="">{NOT_SET}</option> : null}
			{FOCUSES.map((focus) => (
				<option key={focus.value} value={focus.value}>
					{focus.word}
				</option>
			))}
		</Select>
	);
}
