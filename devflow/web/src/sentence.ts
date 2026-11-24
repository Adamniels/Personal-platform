/** Text as a sentence: trimmed, with a capital first letter and a full
 * stop, unless it already ends in one. Reasons come from the server both as
 * sentences and as clauses, such as a worker's "it reached the most turns". */
export function sentence(text: string): string {
	const trimmed = text.trim();
	const capital = trimmed.charAt(0).toUpperCase() + trimmed.slice(1);
	return /[.!?]$/.test(capital) ? capital : `${capital}.`;
}
