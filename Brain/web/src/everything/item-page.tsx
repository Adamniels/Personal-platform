// An item's own page, at /brain/everything/<id>: its full text and its facts, its state, why the
// brain holds it, the events behind it as evidence, its versions and what it is related to. The
// way back keeps the list's filters, which the address carries. "Editing changes provenance, and
// every edit records a judgment" in Brain/docs/trust-model.md is why each correction and
// retirement shows here as an event.
import {
	Badge,
	EmptyState,
	Status,
	Timeline,
	TimelineEntry,
} from "@platform/ui";
import { type ReactNode, useId } from "react";
import { useSearch } from "../address";
import { formatWhen } from "../entries/entry-list";
import section from "../entries/section.module.css";
import { writtenWhen } from "../history/when";
import { Link } from "../link";
import { EVERYTHING_PATH, itemPath } from "./everything-paths";
import page from "./item-page.module.css";
import {
	type ItemDetail,
	type ListedItem,
	useEverything,
	useItem,
	useVersions,
} from "./queries";
import { stateOf } from "./state";
import {
	dayOf,
	kindWord,
	originWords,
	provenanceWord,
	storeWord,
	verbWords,
} from "./words";

export function ItemPage({ id }: { id: string }) {
	const search = useSearch();
	// Every item, which the table has fetched already, says whether this one
	// exists, how it stands now, and what its relations point at.
	const everything = useEverything();

	if (everything.isPending) {
		return <p className={page.note}>Loading the entry…</p>;
	}
	if (everything.isError) {
		return (
			<EmptyState
				title="The entry could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}
	const listed = everything.data.find((item) => item.id === id);
	if (!listed) {
		return (
			<EmptyState
				title="There is no such entry"
				detail="The address names nothing the brain holds for you."
				action={
					<Link className={page.link} href={`${EVERYTHING_PATH}${search}`}>
						Go to Everything
					</Link>
				}
			/>
		);
	}
	return (
		<ItemView
			// Another item is a new page: nothing open carries over.
			key={listed.id}
			listed={listed}
			everything={everything.data}
			search={search}
		/>
	);
}

function ItemView({
	listed,
	everything,
	search,
}: {
	listed: ListedItem;
	everything: readonly ListedItem[];
	search: string;
}) {
	const detail = useItem(listed.id);
	const state = stateOf(listed);
	const back = `${EVERYTHING_PATH}${search}`;

	return (
		<div className={section.section}>
			<nav aria-label="Breadcrumb">
				<ol className={page.breadcrumb}>
					<li>
						<Link className={page.link} href={back}>
							Everything
						</Link>
					</li>
					<li aria-current="page">Entry</li>
				</ol>
			</nav>
			<div className={page.head}>
				<h2 tabIndex={-1} className={page.title}>
					{listed.content}
				</h2>
				<Status tone={state.tone}>{state.word}</Status>
			</div>
			<ul className={page.facts} aria-label="Facts">
				<li>{kindWord(listed.kind)}</li>
				<li>{storeWord(listed.store)}</li>
				<li>{provenanceWord(listed.provenance)}</li>
				<li>
					<time dateTime={listed.created_at}>
						{formatWhen(listed.created_at)}
					</time>
				</li>
			</ul>
			{listed.topics.length > 0 || listed.entities.length > 0 ? (
				<ul className={page.about} aria-label="About">
					{[...listed.topics, ...listed.entities].map((subject) => (
						<li key={subject.id}>
							<Badge>{subject.name}</Badge>
						</li>
					))}
				</ul>
			) : null}
			{detail.isPending ? (
				<p className={page.note}>Loading why the brain holds this…</p>
			) : detail.isError ? (
				<p className={page.note}>
					Why the brain holds this could not be loaded. Reload the page to try
					again.
				</p>
			) : (
				<>
					<Part title="Why the brain holds this">
						<Evidence detail={detail.data} />
					</Part>
					<Versions id={listed.id} search={search} />
					<Related
						detail={detail.data}
						everything={everything}
						search={search}
					/>
				</>
			)}
		</div>
	);
}

/** A part of the page, a region named by its heading. */
function Part({ title, children }: { title: string; children: ReactNode }) {
	const heading = useId();
	return (
		<section className={page.part} aria-labelledby={heading}>
			<h3 id={heading} className={page.partTitle}>
				{title}
			</h3>
			{children}
		</section>
	);
}

/** The events behind the item, oldest first, each what was done, what it
 * says, where it came from and when. */
function Evidence({ detail }: { detail: ItemDetail }) {
	return (
		<Timeline aria-label="Why the brain holds this">
			{detail.evidence.map(({ id, event }) => (
				<TimelineEntry
					key={id}
					time={writtenWhen(event.occurred_at, event.occurred_precision)}
					detail={
						<>
							<span className={page.evidence}>{event.evidence}</span>
							<span className={page.origin}>{originWords(event.origin)}</span>
						</>
					}
				>
					{verbWords(event.verb, event.origin)}
				</TimelineEntry>
			))}
		</Timeline>
	);
}

/** Every version of the entry, oldest first, when there is more than one:
 * each its text, when it was made and its state, and a link to its page
 * but for this one. */
function Versions({ id, search }: { id: string; search: string }) {
	const versions = useVersions(id);
	if (versions.isPending || versions.isError) return null;
	if (versions.data.length < 2) return null;
	return (
		<Part title="Versions">
			<ol className={page.versions}>
				{versions.data.map((version) => {
					const state = stateOf({ ...version, stale: false });
					return (
						<li key={version.id} className={page.version}>
							{version.id === id ? (
								<p className={page.text}>
									{version.content}{" "}
									<span className={page.here}>(this version)</span>
								</p>
							) : (
								<Link
									className={page.link}
									href={`${itemPath(version.id)}${search}`}
								>
									{version.content}
								</Link>
							)}
							<span className={page.versionFacts}>
								<time className={page.when} dateTime={version.created_at}>
									{formatWhen(version.created_at)}
								</time>
								<Status tone={state.tone}>{state.word}</Status>
							</span>
						</li>
					);
				})}
			</ol>
		</Part>
	);
}

/** What a relation says of the item, by its kind and which end it is. */
function relationWord(kind: string, from: boolean): string | undefined {
	if (kind === "supersedes") return from ? "Replaces" : "Replaced by";
	if (kind === "contradicts") return "Contradicts";
	return undefined;
}

/** The relations that hold: what the item replaces, what replaced it, and
 * what it contradicts, either way round, each a link to the other's page. */
function Related({
	detail,
	everything,
	search,
}: {
	detail: ItemDetail;
	everything: readonly ListedItem[];
	search: string;
}) {
	const held = detail.relations.flatMap((relation) => {
		if (relation.ended_at !== null) return [];
		const from = relation.from_item === detail.id;
		const word = relationWord(relation.kind, from);
		const otherId = from ? relation.to_item : relation.from_item;
		const other = everything.find(({ id }) => id === otherId);
		return word && other ? [{ relation, word, other }] : [];
	});
	if (held.length === 0) return null;
	return (
		<Part title="Related">
			<ul className={page.related}>
				{held.map(({ relation, word, other }) => (
					<li key={relation.id} className={page.relation}>
						<span className={page.relationWord}>{word}</span>
						<Link className={page.link} href={`${itemPath(other.id)}${search}`}>
							{other.content}
						</Link>
						<span className={page.when}>{dayOf(relation.created_at)}</span>
					</li>
				))}
			</ul>
		</Part>
	);
}
