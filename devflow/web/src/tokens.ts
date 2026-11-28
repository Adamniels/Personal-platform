// How a count of tokens is said in short where room is tight: Running now's runs, and the daily
// budget's item on "Waiting for you". What a token is, input plus cache writes plus output, is
// Dev flow's one definition, which the server sends; see "Usage and history" in devflow/README.md.

/** A count of tokens, short: "950", "1.5 k", "120 k", "2.1 M". */
export function amount(n: number): string {
	const one = (value: number) => value.toFixed(1).replace(/\.0$/, "");
	if (n < 1000) return String(n);
	if (n < 10_000) return `${one(n / 1000)} k`;
	// Below 999,500, so a count that rounds to 1000 k reads "1 M".
	if (n < 999_500) return `${Math.round(n / 1000)} k`;
	return `${one(n / 1_000_000)} M`;
}
