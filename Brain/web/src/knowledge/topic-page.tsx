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
import { type FormEvent, useEffect, useId, useRef, useState } from "react";
import { navigate } from "../address";
import { formatWhen } from "../entries/entry-list";
import section from "../entries/section.module.css";
import { Link } from "../link";
import { isFieldError } from "../providers";
import { type Choice, ChooseTopic, SameAsDialog } from "./choose-topic";
import { FocusSelect, LevelSelect } from "./fields";
import { KNOWLEDGE_PATH, topicPath } from "./knowledge-paths";
import { focusWord, levelMeaning, levelWord, NOT_SET } from "./levels";
import {
	type KnowledgeRow,
	misconceptionOf,
	type Topic,
	type TopicRelation,
	useAddMisconception,
	useEndRelation,
	useKnowledge,
	usePlaceUnder,
	useRelations,
	useSameAs,
	useSaveSettings,
	useSeparate,
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
	const separate = useSeparate();
	const end = useEndRelation();

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
			<Facts topic={topic} row={row} topics={topics} />
			<div className={styles.grid}>
				<LevelHistory slug={topic.slug} />
				<Misconceptions slug={topic.slug} />
				<Card title="Also called">
					<NameList
						items={aliases.map((alias) => ({ key: alias.id, topic: alias }))}
						empty="No other names."
						link={false}
						action="Separate"
						onAction={({ topic: alias }) => separate.mutateAsync(alias.id)}
					/>
				</Card>
				<Card title="Under">
					<NameList
						items={under.map(({ relation, topic: other }) => ({
							key: relation.id,
							topic: other,
						}))}
						empty="Not under another topic."
						action="Remove"
						onAction={({ key }) => end.mutateAsync(key)}
					/>
				</Card>
				<Card title="Includes">
					<NameList
						items={includes.map(({ relation, topic: other }) => ({
							key: relation.id,
							topic: other,
						}))}
						empty="Includes no other topic."
						action="Remove"
						onAction={({ key }) => end.mutateAsync(key)}
					/>
				</Card>
			</div>
		</div>
	);
}

/** What the topic's actions open in place: its level and focus, or the
 * other topic to place it under or treat it as. */
type Open = "edit" | "under" | "same" | null;

/** The topic's level and focus, with Edit, which opens both in place, and
 * Place under… and Same as…, which each ask for another topic. */
function Facts({
	topic,
	row,
	topics,
}: {
	topic: Topic;
	row: KnowledgeRow | undefined;
	topics: readonly Topic[];
}) {
	const [open, setOpen] = useState<Open>(null);
	// The button whose form just closed, so focus goes back to it.
	const [refocus, setRefocus] = useState<Exclude<Open, null> | null>(null);
	const buttons = {
		edit: useRef<HTMLButtonElement>(null),
		under: useRef<HTMLButtonElement>(null),
		same: useRef<HTMLButtonElement>(null),
	};
	const [target, setTarget] = useState<Choice | null>(null);
	const placeUnder = usePlaceUnder();
	const sameAs = useSameAs();
	const toast = useToast();

	useEffect(() => {
		if (refocus === null || open !== null) return;
		buttons[refocus].current?.focus();
		setRefocus(null);
	});

	function close() {
		if (open !== null) setRefocus(open);
		setOpen(null);
	}

	// The other canonical topics, for Place under… and Same as… alike: not
	// an alias, which the brain refuses to point at, and not a provisional
	// topic, which he has not confirmed, so nothing is built on it.
	const choices: Choice[] = topics
		.filter(
			(other) =>
				other.id !== topic.id &&
				other.alias_of === null &&
				other.state === "canonical",
		)
		.map(({ id, name }) => ({ id, name }))
		.sort((a, b) => a.name.localeCompare(b.name, "en"));

	const level = row?.level?.value;
	const focus = row?.focus?.value;
	return (
		<>
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
				{open === null ? (
					<div className={styles.actions}>
						<Button ref={buttons.edit} onClick={() => setOpen("edit")}>
							Edit
						</Button>
						<Button ref={buttons.under} onClick={() => setOpen("under")}>
							Place under…
						</Button>
						<Button ref={buttons.same} onClick={() => setOpen("same")}>
							Same as…
						</Button>
					</div>
				) : null}
			</div>
			{open === null ? null : (
				<div className={`${section.panel} ${section.form}`}>
					{open === "edit" ? (
						<SettingsForm topic={topic} row={row} onDone={close} />
					) : open === "under" ? (
						<ChooseTopic
							name="Place under…"
							label="Place under"
							choices={choices}
							onChoose={async (broader) => {
								await placeUnder.mutateAsync({
									id: topic.id,
									broaderId: broader.id,
								});
								toast.success(`Placed under ${broader.name}`);
								close();
							}}
							onCancel={close}
						/>
					) : (
						<ChooseTopic
							name="Same as…"
							label="Same as"
							choices={choices}
							onChoose={setTarget}
							onCancel={close}
						/>
					)}
				</div>
			)}
			{target ? (
				<SameAsDialog
					topic={topic.name}
					target={target.name}
					onConfirm={async () => {
						try {
							await sameAs.mutateAsync({ id: topic.id, targetId: target.id });
							toast.success(`Treated as ${target.name}`);
						} finally {
							setTarget(null);
						}
					}}
					onCancel={() => setTarget(null)}
				/>
			) : null}
		</>
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

type Named = { key: string; topic: Topic };

/** A list of topics by name, each linking to its page unless `link` is
 * false, with one action at its end, or a line saying there are none. */
function NameList({
	items,
	empty,
	link = true,
	action,
	onAction,
}: {
	items: readonly Named[];
	empty: string;
	link?: boolean;
	/** The action's word, "Separate" or "Remove". */
	action: string;
	onAction: (item: Named) => Promise<unknown>;
}) {
	const id = useId();
	if (items.length === 0) return <p className={styles.note}>{empty}</p>;
	return (
		<ul className={styles.list}>
			{items.map((item) => {
				const nameId = `${id}-${item.key}`;
				return (
					<li key={item.key} className={styles.named}>
						{link ? (
							<Link
								id={nameId}
								className={styles.link}
								href={topicPath(item.topic.slug)}
							>
								{item.topic.name}
							</Link>
						) : (
							<span id={nameId} className={styles.alias}>
								{item.topic.name}
							</span>
						)}
						<Button
							kind="ghost"
							aria-describedby={nameId}
							onClick={() =>
								onAction(item).catch(() => {
									// The failure has raised its toast.
								})
							}
						>
							{action}
						</Button>
					</li>
				);
			})}
		</ul>
	);
}
