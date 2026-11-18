// Screenshots, paired by view, as thumbnails a tap opens large: a runner item's, the ones its pull
// request's body showed of its own, and a card's, the pictures the worker took of its head commit.
// The images are read from Dev flow, which reads them from the code host as the app and serves
// them only as PNGs, never from the addresses in a body, which need a GitHub session. See "What
// waits" and "Screenshots" in devflow/README.md.
import { Dialog } from "@platform/ui";
import { useId, useState } from "react";
import styles from "./screenshots.module.css";
import type { WaitingItem } from "./waiting";
import waiting from "./waiting.module.css";

type Group = WaitingItem["screenshots"][number];

/** Where the page reads one of a runner item's screenshots, its name one
 * segment whatever it holds. */
export function waitingScreenshot(item: string, name: string): string {
	return `/devflow/api/waiting/${encodeURIComponent(item)}/screenshots/${encodeURIComponent(name)}`;
}

/** Where the page reads one of the pictures of a card's head commit, each
 * part one segment whatever it holds. */
export function cardScreenshot(
	project: string,
	card: number,
	name: string,
): string {
	return `/devflow/api/projects/${encodeURIComponent(project)}/cards/${card}/screenshots/${encodeURIComponent(name)}`;
}

/** The row "Screenshots": each pair under its name, the desktop one and the
 * phone one as thumbnails, the one tapped open in a dialog at its natural
 * size, and the note, as text, when the worker said why some or all could
 * not be taken. Nothing with neither. Untitled, for a panel that is titled
 * Screenshots itself, the list keeps the name. */
export function Screenshots({
	address,
	groups,
	note = null,
	titled = true,
}: {
	/** Where a screenshot is read, by its name. */
	address: (name: string) => string;
	groups: Group[];
	note?: string | null;
	titled?: boolean;
}) {
	const id = useId();
	const [open, setOpen] = useState<string | null>(null);
	if (groups.length === 0 && note === null) return null;

	return (
		<div className={waiting.block}>
			{titled ? (
				<h3 id={id} className={waiting.label}>
					Screenshots
				</h3>
			) : null}
			{note === null ? null : <p className={styles.note}>{note}</p>}
			{groups.length === 0 ? null : (
				<ul
					aria-labelledby={titled ? id : undefined}
					aria-label={titled ? undefined : "Screenshots"}
					className={styles.groups}
				>
					{groups.map((group) => (
						<li key={group.name} className={styles.group}>
							<p className={styles.name}>{group.name}</p>
							<div className={styles.thumbnails}>
								{group.images.map((name) => (
									<Shot
										key={name}
										name={name}
										src={address(name)}
										onOpen={() => setOpen(name)}
									/>
								))}
							</div>
						</li>
					))}
				</ul>
			)}
			{open === null ? null : (
				<Dialog
					open
					title={open}
					onCancel={() => setOpen(null)}
					className={styles.viewer}
				>
					<div className={styles.frame}>
						<Image name={open} src={address(open)} />
					</div>
				</Dialog>
			)}
		</div>
	);
}

/** A thumbnail, a button that opens it, or its name and that it could not
 * be loaded. */
function Shot({
	name,
	src,
	onOpen,
}: {
	name: string;
	src: string;
	onOpen: () => void;
}) {
	const [failed, setFailed] = useState(false);
	if (failed) return <Failed name={name} />;
	return (
		<button type="button" className={styles.thumbnail} onClick={onOpen}>
			<img
				src={src}
				alt={name}
				loading="lazy"
				className={styles.small}
				onError={() => setFailed(true)}
			/>
		</button>
	);
}

/** The image opened, at its natural size, or its name and that it could
 * not be loaded. */
function Image({ name, src }: { name: string; src: string }) {
	const [failed, setFailed] = useState(false);
	if (failed) return <Failed name={name} />;
	return (
		<img
			src={src}
			alt={name}
			className={styles.large}
			onError={() => setFailed(true)}
		/>
	);
}

function Failed({ name }: { name: string }) {
	return <p className={styles.failed}>{name} could not be loaded</p>;
}
