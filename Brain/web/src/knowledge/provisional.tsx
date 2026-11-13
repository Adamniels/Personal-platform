// The topics that arrived on their own: provisional ones, which the brain makes silently when a
// feature sends an event about a topic it has not seen, as "Topic vocabulary" in
// Brain/docs/event-model.md has it. Each is promoted to a topic of its own, or treated as one he
// has already.
import { Button, Card, useToast } from "@platform/ui";
import { useId, useState } from "react";
import { Link } from "../link";
import { type Choice, ChooseTopic, SameAsDialog } from "./choose-topic";
import { topicPath } from "./knowledge-paths";
import { type KnowledgeRow, usePromote, useSameAs } from "./queries";
import styles from "./topic-page.module.css";

export function Provisional({
	rows,
	canonical,
}: {
	/** The provisional topics, at least one. */
	rows: readonly KnowledgeRow[];
	/** The topics one may be treated as. */
	canonical: readonly KnowledgeRow[];
}) {
	const choices = canonical.map(({ id, name }) => ({ id, name }));
	return (
		<Card title="Topics that arrived on their own">
			<div className={styles.body}>
				<p className={styles.note}>
					Features create these when they send something about a topic the brain
					has not seen. Promote one to keep it as a topic, or treat it as one
					you have.
				</p>
				<ul className={styles.list}>
					{rows.map((row) => (
						<ProvisionalTopic key={row.id} row={row} choices={choices} />
					))}
				</ul>
			</div>
		</Card>
	);
}

function ProvisionalTopic({
	row,
	choices,
}: {
	row: KnowledgeRow;
	choices: readonly Choice[];
}) {
	const promote = usePromote();
	const sameAs = useSameAs();
	const toast = useToast();
	const [choosing, setChoosing] = useState(false);
	const [target, setTarget] = useState<Choice | null>(null);
	const nameId = useId();

	return (
		<li className={styles.named}>
			<Link id={nameId} className={styles.link} href={topicPath(row.slug)}>
				{row.name}
			</Link>
			{choosing ? null : (
				<div className={styles.actions}>
					<Button
						aria-describedby={nameId}
						onClick={() =>
							promote
								.mutateAsync(row.id)
								.then(() => toast.success(`${row.name} promoted`))
								.catch(() => {
									// The failure has raised its toast.
								})
						}
					>
						Promote
					</Button>
					<Button
						kind="ghost"
						aria-describedby={nameId}
						onClick={() => setChoosing(true)}
					>
						Same as…
					</Button>
				</div>
			)}
			{choosing ? (
				<div className={styles.inline}>
					<ChooseTopic
						name="Same as…"
						label="Same as"
						choices={choices}
						onChoose={setTarget}
						onCancel={() => setChoosing(false)}
					/>
				</div>
			) : null}
			{target ? (
				<SameAsDialog
					topic={row.name}
					target={target.name}
					onConfirm={async () => {
						try {
							await sameAs.mutateAsync({ id: row.id, targetId: target.id });
							toast.success(`Treated as ${target.name}`);
						} finally {
							setTarget(null);
						}
					}}
					onCancel={() => setTarget(null)}
				/>
			) : null}
		</li>
	);
}
