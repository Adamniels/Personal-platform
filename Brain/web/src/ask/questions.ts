// The brain's four questions, as the Ask section asks them, and the body each sends. Every one is a
// POST under /ask/ that changes nothing, and each declares whether it is about now or about
// history, "Response shape" in "Retrieval: per feature calls over shared primitives" in
// Brain/docs/brain-architecture.md. The routes are "Asking" in Brain/README.md.
import type { components } from "../api/schema";
import type { Subject } from "../everything/filters";

type Schemas = components["schemas"];

export type QuestionKey = "profile" | "topics" | "history" | "now";

export type Question = {
	key: QuestionKey;
	/** The tab, in plain words. */
	label: string;
};

export const QUESTIONS: readonly Question[] = [
	{ key: "profile", label: "Who is this person?" },
	{ key: "topics", label: "What do they know?" },
	{ key: "history", label: "What is the history?" },
	{ key: "now", label: "What is going on now?" },
];

export type Mode = "now" | "history";

export const MODES: readonly { value: Mode; word: string }[] = [
	{ value: "now", word: "Now" },
	{ value: "history", word: "History" },
];

/** The most topics one question takes, the brain's own limit. */
export const MAX_TOPICS = 50;

/** What a question's form holds: the topics chosen, by slug, in the order
 * chosen, for What do they know?, and the one subject for What is the
 * history?. Each question reads only its own. */
export type Draft = { topics: readonly string[]; subject: Subject | null };

export const EMPTY_DRAFT: Draft = { topics: [], subject: null };

/** What each question answers, by its key. */
export type Answers = {
	profile: Schemas["ProfileAnswer"];
	topics: Schemas["TopicsAnswer"];
	history: Schemas["HistoryAnswer"];
	now: Schemas["NowAnswer"];
};

/** An answer with the question it answers, so it is read as its shape. */
export type Asked = {
	[K in QuestionKey]: { question: K; answer: Answers[K] };
}[QuestionKey];

/** The body a question sends, or what is missing from its form. */
export function bodyOf(
	question: QuestionKey,
	mode: Mode,
	draft: Draft,
): { body: Record<string, unknown> } | { missing: string } {
	if (question === "topics") {
		if (draft.topics.length === 0) return { missing: "Choose a topic first" };
		return { body: { mode, topics: [...draft.topics] } };
	}
	if (question === "history") {
		const { subject } = draft;
		if (subject === null) {
			return { missing: "Choose a topic or a named thing first" };
		}
		return {
			body:
				subject.kind === "topic"
					? { mode, topic: subject.slug }
					: { mode, entity: subject.name },
		};
	}
	return { body: { mode } };
}
