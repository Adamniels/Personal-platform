// A decision's own page, at /brain/history/<id>: what was decided, why, what it was made over,
// when, and its place among the others, what it replaces and what replaced it. A replaced
// decision stays readable, since that it was made is permanently true, and only the claim that it
// holds has ended, "Decisions can go stale" in Brain/docs/event-model.md.
import {
	Button,
	Card,
	EmptyState,
	Select,
	Status,
	useToast,
} from "@platform/ui";
import { type FormEvent, useEffect, useRef, useState } from "react";
import section from "../entries/section.module.css";
import { Link } from "../link";
import { SubjectBadges } from "../subjects/subject-fields";
import page from "./decision-page.module.css";
import { decisionOf } from "./decisions";
import { decisionPath, HISTORY_PATH } from "./history-paths";
import { type HistoryRow, useHistory, useReplace } from "./queries";
import { writtenWhen } from "./when";

const REPLACE = "Replace with a newer decision…";

export function DecisionPage({ id }: { id: string }) {
	// The whole history, which the section has fetched already, holds every
	// decision there is to show, replaced ones included, with what replaced
	// them.
	const history = useHistory();

	if (history.isPending) {
		return <p className={page.note}>Loading the decision…</p>;
	}
	if (history.isError) {
		return (
			<EmptyState
				title="The decision could not be loaded"
				detail="Reload the page to try again."
			/>
		);
	}
	const decision = history.data.find(
		(row) => row.id === id && row.kind === "decision",
	);
	if (!decision) {
		return (
			<EmptyState
				title="There is no such decision"
				detail="The address names no decision you have."
				action={
					<Link className={page.link} href={HISTORY_PATH}>
						Go to History
					</Link>
				}
			/>
		);
	}
	return (
		<DecisionView
			// Another decision is a new page: nothing open carries over.
			key={decision.id}
			decision={decision}
			decisions={history.data.filter((row) => row.kind === "decision")}
		/>
	);
}

function DecisionView({
	decision,
	decisions,
}: {
	decision: HistoryRow;
	decisions: readonly HistoryRow[];
}) {
	const { reasoning, rejected } = decisionOf(decision) ?? {
		reasoning: "",
		rejected: [],
	};
	const replacedBy = decisions.find(({ id }) => id === decision.replaced_by);
	const replaces = decisions.filter(
		({ replaced_by }) => replaced_by === decision.id,
	);
	const [replacing, setReplacing] = useState(false);
	const heading = useRef<HTMLHeadingElement>(null);
	const replaceButton = useRef<HTMLButtonElement>(null);
	// Where focus goes once the form closes: back to the button, or, once
	// the decision is replaced and the button is gone, to the heading.
	const [refocus, setRefocus] = useState<"button" | "heading" | null>(null);

	useEffect(() => {
		if (refocus === null || replacing) return;
		(refocus === "button" ? replaceButton : heading).current?.focus();
		setRefocus(null);
	}, [refocus, replacing]);

	const when = decision.occurred_at
		? writtenWhen(
				decision.occurred_at,
				decision.occurred_precision ?? "instant",
			)
		: "";

	return (
		<div className={section.section}>
			<nav aria-label="Breadcrumb">
				<ol className={page.breadcrumb}>
					<li>
						<Link className={page.link} href={HISTORY_PATH}>
							History
						</Link>
					</li>
					<li aria-current="page">Decision</li>
				</ol>
			</nav>
			<div className={page.head}>
				<h2 ref={heading} tabIndex={-1} className={page.title}>
					{decision.content}
				</h2>
				{replacedBy ? <Status tone="idle">Replaced</Status> : null}
			</div>
			<div className={page.facts}>
				<dl className={page.list}>
					<div>
						<dt>Decided</dt>
						<dd className={page.when}>{when}</dd>
					</div>
					{decision.topics.length > 0 || decision.entities.length > 0 ? (
						<div>
							<dt>About</dt>
							<dd>
								<SubjectBadges
									topics={decision.topics}
									entities={decision.entities}
								/>
							</dd>
						</div>
					) : null}
					{replaces.length > 0 ? (
						<div>
							<dt>Replaces</dt>
							<dd className={page.links}>
								{replaces.map((older) => (
									<Link
										key={older.id}
										className={page.link}
										href={decisionPath(older.id)}
									>
										{older.content}
									</Link>
								))}
							</dd>
						</div>
					) : null}
					{replacedBy ? (
						<div>
							<dt>Replaced by</dt>
							<dd className={page.links}>
								<Link className={page.link} href={decisionPath(replacedBy.id)}>
									{replacedBy.content}
								</Link>
							</dd>
						</div>
					) : null}
				</dl>
				{replacedBy || replacing ? null : (
					<Button ref={replaceButton} onClick={() => setReplacing(true)}>
						{REPLACE}
					</Button>
				)}
			</div>
			{replacing ? (
				<div className={`${section.panel} ${section.form}`}>
					<ChooseNewer
						decision={decision}
						choices={decisions.filter(
							(other) => other.id !== decision.id && other.status === "active",
						)}
						onDone={(replaced) => {
							setReplacing(false);
							setRefocus(replaced ? "heading" : "button");
						}}
					/>
				</div>
			) : null}
			<div className={page.grid}>
				<Card title="Why">
					<p className={page.text}>{reasoning}</p>
				</Card>
				<Card title="What was rejected">
					{rejected.length === 0 ? (
						<p className={page.note}>Nothing recorded.</p>
					) : (
						<ul className={page.rejected}>
							{rejected.map((option, index) => (
								// Options may repeat, and never move.
								// biome-ignore lint/suspicious/noArrayIndexKey: see above
								<li key={index} className={page.text}>
									{option}
								</li>
							))}
						</ul>
					)}
				</Card>
			</div>
		</div>
	);
}

/** A select of the other active decisions, with Save and Cancel. */
function ChooseNewer({
	decision,
	choices,
	onDone,
}: {
	decision: HistoryRow;
	choices: readonly HistoryRow[];
	/** Called when the form closes, with whether it replaced the decision. */
	onDone: (replaced: boolean) => void;
}) {
	const replace = useReplace();
	const toast = useToast();
	const [chosen, setChosen] = useState("");
	const [error, setError] = useState<string | undefined>();
	const select = useRef<HTMLSelectElement>(null);

	useEffect(() => {
		select.current?.focus();
	}, []);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (replace.isPending) return;
		if (chosen === "") {
			setError("Choose the newer decision");
			return;
		}
		try {
			await replace.mutateAsync({ id: decision.id, replacedBy: chosen });
		} catch {
			// The failure has raised its toast, and the form stays open.
			return;
		}
		toast.success("Decision replaced");
		onDone(true);
	}

	return (
		<form
			className={page.form}
			aria-label={REPLACE}
			noValidate
			onSubmit={submit}
		>
			<Select
				ref={select}
				label="Newer decision"
				help={
					choices.length === 0
						? "Add the newer decision on History first."
						: undefined
				}
				value={chosen}
				error={error}
				onChange={(event) => {
					setChosen(event.target.value);
					setError(undefined);
				}}
			>
				<option value="" disabled>
					{choices.length === 0 ? "No other decision yet" : "Choose a decision"}
				</option>
				{choices.map((choice) => (
					<option key={choice.id} value={choice.id}>
						{choice.content}
					</option>
				))}
			</Select>
			<div className={page.actions}>
				<Button type="submit" disabled={replace.isPending}>
					Save
				</Button>
				<Button kind="ghost" onClick={() => onDone(false)}>
					Cancel
				</Button>
			</div>
		</form>
	);
}
