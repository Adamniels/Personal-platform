// A topic's own page, at /brain/knowledge/<slug>: its level and focus, the versions of its level,
// the misconceptions he had about it, its other names, and the topics it is under and includes.
// An alias has no page of its own: it answers under its target, so its address moves there.
import {
	Badge,
	Button,
	Card,
	EmptyState,
	TextArea,
	Timeline,
	TimelineEntry,
	useToast,
} from "@platform/ui";
import { type FormEvent, useEffect, useRef, useState } from "react";
import { navigate } from "../address";
import { formatWhen } from "../entries/entry-list";
import section from "../entries/section.module.css";
import { Link } from "../link";
import { isFieldError } from "../providers";
import { FocusSelect, LevelSelect } from "./fields";
import { KNOWLEDGE_PATH, topicPath } from "./knowledge-paths";
import { focusWord, levelMeaning, levelWord, NOT_SET } from "./levels";
import {
	type KnowledgeRow,
	misconceptionOf,
	type Topic,
	type TopicRelation,
	useAddMisconception,
	useKnowledge,
	useRelations,
	useSaveSettings,
	useTopicItems,
	useTopics,
} from "./queries";
import styles from "./topic-page.module.css";

export function TopicPage({ slug }: { slug: string }) {
	const topics = useTopics();
	const knowledge = useKnowledge();
	const relations = useRelations();

	const topic = topics.data?.find((each) => each.slug === slug);
	// What the page is about: the topic, or the target it is an alias of.
	const shown =
		topic && topics.data?.find(({ id }) => id === topic.resolves_to);

	useEffect(() => {
		if (shown && shown.slug !== slug) {
			navigate(topicPath(shown.slug), { replace: true });
		}
	}, [shown, slug]);

	if (topics.isPending || knowledge.isPending || relations.isPending) {
		return <p className={styles.note}>Loading the topic…</p>;
	}
	if (topics.isError || knowledge.isError || relations.isError) {
		return (
			<EmptyState
				title="The topic could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}
	if (!shown) {
		return (
			<EmptyState
				title="There is no such topic"
				detail="The address names no topic you have."
				action={
					<Link className={styles.link} href={KNOWLEDGE_PATH}>
						Go to Knowledge
					</Link>
				}
			/>
		);
	}

	return (
		<TopicView
			// A new topic is a new page: nothing open carries over.
			key={shown.id}
			topic={shown}
			row={knowledge.data.find(({ id }) => id === shown.id)}
			topics={topics.data}
			relations={relations.data}
		/>
	);
}

/** A relation that holds, with the topic at its other end, read through
 * aliases. */
type Related = { relation: TopicRelation; topic: Topic };

function TopicView({
	topic,
	row,
	topics,
	relations,
}: {
	topic: Topic;
	/** Its level and focus; missing only between a change and the refresh. */
	row: KnowledgeRow | undefined;
	topics: readonly Topic[];
	relations: readonly TopicRelation[];
}) {
	const byId = new Map(topics.map((each) => [each.id, each]));
	const resolved = (id: string) => {
		const found = byId.get(id);
		return found && byId.get(found.resolves_to);
	};
	const broader = relations.filter(({ kind }) => kind === "broader_than");
	const ends = (
		own: "from_topic" | "to_topic",
		other: "from_topic" | "to_topic",
	): Related[] =>
		broader.flatMap((relation) => {
			const other_ = resolved(relation[other]);
			return resolved(relation[own])?.id === topic.id && other_
				? [{ relation, topic: other_ }]
				: [];
		});
	// Under: the broader end of every relation whose narrower end is this.
	const under = ends("to_topic", "from_topic");
	const includes = ends("from_topic", "to_topic");
	const aliases = topics.filter(({ alias_of }) => alias_of === topic.id);

	return (
		<div className={section.section}>
			<nav aria-label="Breadcrumb">
				<ol className={styles.breadcrumb}>
					<li>
						<Link className={styles.link} href={KNOWLEDGE_PATH}>
							Knowledge
						</Link>
					</li>
					<li aria-current="page">{topic.name}</li>
				</ol>
			</nav>
			<div className={styles.head}>
				<h2 className={styles.name}>{topic.name}</h2>
				{topic.state === "provisional" ? <Badge>Provisional</Badge> : null}
			</div>
			<Settings topic={topic} row={row} />
			<div className={styles.grid}>
				<LevelHistory slug={topic.slug} />
				<Misconceptions slug={topic.slug} />
				<Card title="Also called">
					<NameList
						items={aliases.map((alias) => ({ key: alias.id, topic: alias }))}
						empty="No other names."
						link={false}
					/>
				</Card>
				<Card title="Under">
					<NameList
						items={under.map(({ relation, topic: other }) => ({
							key: relation.id,
							topic: other,
						}))}
						empty="Not under another topic."
					/>
				</Card>
				<Card title="Includes">
					<NameList
						items={includes.map(({ relation, topic: other }) => ({
							key: relation.id,
							topic: other,
						}))}
						empty="Includes no other topic."
					/>
				</Card>
			</div>
		</div>
	);
}

/** The topic's level and focus, with Edit, which opens both in place. */
function Settings({
	topic,
	row,
}: {
	topic: Topic;
	row: KnowledgeRow | undefined;
}) {
	const [editing, setEditing] = useState(false);
	// Set when the form closes, so focus goes back to Edit.
	const [refocus, setRefocus] = useState(false);
	const editButton = useRef<HTMLButtonElement>(null);

	useEffect(() => {
		if (!refocus || editing) return;
		editButton.current?.focus();
		setRefocus(false);
	}, [refocus, editing]);

	function stop() {
		setEditing(false);
		setRefocus(true);
	}

	const level = row?.level?.value;
	const focus = row?.focus?.value;
	if (editing) {
		return (
			<div className={`${section.panel} ${section.form}`}>
				<SettingsForm topic={topic} row={row} onDone={stop} />
			</div>
		);
	}
	return (
		<div className={styles.facts}>
			<dl className={styles.settings}>
				<div>
					<dt>Level</dt>
					<dd>
						<span className={level ? undefined : styles.unset}>
							{levelWord(level)}
						</span>
						{level ? (
							<span className={styles.meaning}>{levelMeaning(level)}</span>
						) : null}
					</dd>
				</div>
				<div>
					<dt>Focus</dt>
					<dd>
						{focus ? (
							<Badge>{focusWord(focus)}</Badge>
						) : (
							<span className={styles.unset}>{NOT_SET}</span>
						)}
					</dd>
				</div>
			</dl>
			<div className={styles.actions}>
				<Button ref={editButton} onClick={() => setEditing(true)}>
					Edit
				</Button>
			</div>
		</div>
	);
}

function SettingsForm({
	topic,
	row,
	onDone,
}: {
	topic: Topic;
	row: KnowledgeRow | undefined;
	onDone: () => void;
}) {
	const save = useSaveSettings();
	const toast = useToast();
	const [level, setLevel] = useState(row?.level?.value ?? "");
	const [focus, setFocus] = useState(row?.focus?.value ?? "");
	const [saving, setSaving] = useState(false);
	const first = useRef<HTMLSelectElement>(null);

	useEffect(() => {
		first.current?.focus();
	}, []);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (saving) return;
		setSaving(true);
		try {
			const changed = await save(
				{
					slug: topic.slug,
					level: row?.level ?? null,
					focus: row?.focus ?? null,
				},
				{ level, focus },
			);
			if (changed) toast.success("Topic saved");
			onDone();
		} catch {
			// The failure has raised its toast, and the form stays open.
		} finally {
			setSaving(false);
		}
	}

	return (
		<form
			className={styles.form}
			aria-label="Level and focus"
			noValidate
			onSubmit={submit}
		>
			<div className={styles.pair}>
				<LevelSelect
					ref={first}
					value={level}
					onChange={setLevel}
					unset={!row?.level}
				/>
				<FocusSelect value={focus} onChange={setFocus} unset={!row?.focus} />
			</div>
			<div className={styles.actions}>
				<Button type="submit" disabled={saving}>
					Save
				</Button>
				<Button kind="ghost" onClick={onDone}>
					Cancel
				</Button>
			</div>
		</form>
	);
}

/** The versions of the topic's level, oldest first, each with when it was
 * set. */
function LevelHistory({ slug }: { slug: string }) {
	const levels = useTopicItems(slug, "topic_level");
	return (
		<Card title="Level over time">
			{levels.isPending ? (
				<p className={styles.note}>Loading…</p>
			) : levels.isError ? (
				<p className={styles.note}>The levels could not be loaded.</p>
			) : levels.data.length === 0 ? (
				<p className={styles.note}>No level set yet.</p>
			) : (
				<Timeline aria-label="Level over time">
					{levels.data.map((item) => (
						<TimelineEntry key={item.id} time={formatWhen(item.created_at)}>
							{levelWord(item.value)}
						</TimelineEntry>
					))}
				</Timeline>
			)}
		</Card>
	);
}

/** What he believed about the topic that is not so, and what is right, with
 * Add misconception. */
function Misconceptions({ slug }: { slug: string }) {
	const history = useTopicItems(slug, "history");
	const [adding, setAdding] = useState(false);
	const found = (history.data ?? []).flatMap((item) => {
		const misconception =
			item.status === "active" ? misconceptionOf(item) : undefined;
		return misconception ? [{ id: item.id, ...misconception }] : [];
	});

	return (
		<Card
			title="Misconceptions"
			footer={
				adding ? undefined : (
					<Button onClick={() => setAdding(true)}>Add misconception</Button>
				)
			}
		>
			<div className={styles.body}>
				{history.isPending ? (
					<p className={styles.note}>Loading…</p>
				) : found.length === 0 ? (
					<p className={styles.note}>None recorded.</p>
				) : (
					<ul className={styles.list}>
						{found.map((misconception) => (
							<li key={misconception.id} className={styles.misconception}>
								<dl>
									<dt>What I believed</dt>
									<dd>{misconception.believed}</dd>
									<dt>What is right</dt>
									<dd>{misconception.correct}</dd>
								</dl>
							</li>
						))}
					</ul>
				)}
				{adding ? (
					<MisconceptionForm slug={slug} onDone={() => setAdding(false)} />
				) : null}
			</div>
		</Card>
	);
}

function MisconceptionForm({
	slug,
	onDone,
}: {
	slug: string;
	onDone: () => void;
}) {
	const add = useAddMisconception();
	const toast = useToast();
	const [believed, setBelieved] = useState("");
	const [correct, setCorrect] = useState("");
	const [errors, setErrors] = useState<{ believed?: string; correct?: string }>(
		{},
	);
	const [saving, setSaving] = useState(false);
	const first = useRef<HTMLTextAreaElement>(null);

	useEffect(() => {
		first.current?.focus();
	}, []);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (saving) return;
		const misconception = {
			believed: believed.trim(),
			correct: correct.trim(),
		};
		const missing = {
			believed:
				misconception.believed === "" ? "Write what you believed" : undefined,
			correct: misconception.correct === "" ? "Write what is right" : undefined,
		};
		setErrors(missing);
		if (missing.believed || missing.correct) return;
		setSaving(true);
		try {
			await add.mutateAsync({ slug, misconception });
			toast.success("Misconception saved");
			onDone();
		} catch (failure) {
			if (isFieldError(failure)) {
				setErrors({
					believed: failure.problem.detail ?? failure.problem.title,
				});
			}
		} finally {
			setSaving(false);
		}
	}

	return (
		<form
			className={styles.form}
			aria-label="Add misconception"
			noValidate
			onSubmit={submit}
		>
			<TextArea
				ref={first}
				label="What I believed"
				rows={2}
				value={believed}
				error={errors.believed}
				onChange={(event) => setBelieved(event.target.value)}
			/>
			<TextArea
				label="What is right"
				rows={2}
				value={correct}
				error={errors.correct}
				onChange={(event) => setCorrect(event.target.value)}
			/>
			<div className={styles.actions}>
				<Button type="submit" disabled={saving}>
					Save
				</Button>
				<Button kind="ghost" onClick={onDone}>
					Cancel
				</Button>
			</div>
		</form>
	);
}

/** A list of topics by name, each linking to its page unless `link` is
 * false, or a line saying there are none. */
function NameList({
	items,
	empty,
	link = true,
}: {
	items: readonly { key: string; topic: Topic }[];
	empty: string;
	link?: boolean;
}) {
	if (items.length === 0) return <p className={styles.note}>{empty}</p>;
	return (
		<ul className={styles.list}>
			{items.map(({ key, topic }) => (
				<li key={key} className={styles.named}>
					{link ? (
						<Link className={styles.link} href={topicPath(topic.slug)}>
							{topic.name}
						</Link>
					) : (
						<span className={styles.alias}>{topic.name}</span>
					)}
				</li>
			))}
		</ul>
	);
}
