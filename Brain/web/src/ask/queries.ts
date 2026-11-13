// What the Ask section sends: one question, when Ask is pressed. It is a mutation rather than a
// query, though nothing changes, because it is sent only on request and never kept: the answer is
// what the brain said at that moment, and asking again asks again. A failure raises its toast in
// src/providers.tsx, as every failed call does.
import { useMutation } from "@tanstack/react-query";
import { useClient } from "../providers";
import type { Asked, QuestionKey } from "./questions";

export function useAsk() {
	const client = useClient();
	return useMutation({
		mutationFn: async ({
			question,
			body,
		}: {
			question: QuestionKey;
			body: Record<string, unknown>;
		}): Promise<Asked> => {
			// Each question's route is its key under /ask/.
			const answer = await client.send("POST", `/ask/${question}`, body);
			return { question, answer } as Asked;
		},
	});
}
