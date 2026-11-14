// The Study section: the start view at /review, and a session at /review/study, its filter in the
// query string. A session is keyed by its address, so a new filter starts a new session. See
// "Studying" in review/README.md.
import { usePathname, useSearch } from "../address";
import { filterOf } from "./study";
import { StudySession } from "./study-session";
import { StudyStart } from "./study-start";

export function StudySection() {
	const pathname = usePathname().replace(/(?<=.)\/+$/, "");
	const search = useSearch();
	if (pathname !== "/review/study") return <StudyStart />;
	const { deck, topic } = filterOf(search);
	return <StudySession key={search} deck={deck} topic={topic} />;
}
