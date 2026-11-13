import { Badge, Select, TextField } from "@platform/ui";
import { useId } from "react";
import type { components } from "../api/schema";
import { type SubjectDraft, useCanonicalTopics, useEntities } from "./subjects";
import styles from "./subjects.module.css";

/** The Topic select and the Named thing field, side by side, both optional.
 * The field offers the named things the user has, and takes a new name as
 * typed. */
export function SubjectFields({
	value,
	onChange,
}: {
	value: SubjectDraft;
	onChange: (value: SubjectDraft) => void;
}) {
	const topics = useCanonicalTopics();
	const entities = useEntities();
	const names = useId();

	return (
		<div className={styles.fields}>
			<Select
				label="Topic"
				value={value.topic}
				onChange={(event) => onChange({ ...value, topic: event.target.value })}
			>
				<option value="">None</option>
				{(topics.data ?? []).map((topic) => (
					<option key={topic.id} value={topic.slug}>
						{topic.name}
					</option>
				))}
			</Select>
			<TextField
				label="Named thing"
				help="One you have, or a new name."
				list={names}
				autoComplete="off"
				value={value.entity}
				onChange={(event) => onChange({ ...value, entity: event.target.value })}
			/>
			<datalist id={names}>
				{(entities.data ?? []).map((entity) => (
					<option key={entity.id} value={entity.name} />
				))}
			</datalist>
		</div>
	);
}

type Subject = { id: string; name: string };

/** An entry's subjects as badges, its topics then its named things, or
 * nothing when it has none. */
export function SubjectBadges({
	topics,
	entities,
}: {
	topics: readonly components["schemas"]["EventTopic"][];
	entities: readonly Subject[];
}) {
	if (topics.length === 0 && entities.length === 0) return null;
	return (
		<span className={styles.badges}>
			{[...topics, ...entities].map((subject) => (
				<Badge key={subject.id}>{subject.name}</Badge>
			))}
		</span>
	);
}
