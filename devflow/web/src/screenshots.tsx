// An item's screenshots: the ones its pull request's body showed of its own, paired by view, as
// thumbnails a tap opens large. The images are read from Dev flow, which reads them from the code
// host as the app and serves them only as PNGs, never from the addresses in the body, which need
// a GitHub session. See "What waits" in devflow/README.md.
import { Dialog } from "@platform/ui";
import { useId, useState } from "react";
import styles from "./screenshots.module.css";
import type { WaitingItem } from "./waiting";
import waiting from "./waiting.module.css";

type Group = WaitingItem["screenshots"][number];

/** Where the page reads one of an item's screenshots, its name one segment
 * whatever it holds. */
function address(item: string, name: string): string {
	return `/devflow/api/waiting/${encodeURIComponent(item)}/screenshots/${encodeURIComponent(name)}`;
}

/** The row "Screenshots": each pair under its name, the desktop one and the
 * phone one as thumbnails, and the one tapped open in a dialog at its
 * natural size. Nothing without screenshots. */
export function Screenshots({
	item,
	groups,
}: {
	/** The item's id. */
	item: string;
	groups: Group[];
}) {
	const id = useId();
	const [open, setOpen] = useState<string | null>(null);
	if (groups.length === 0) return null;

	return (
		<div className={waiting.block}>
			<h3 id={id} className={waiting.label}>
				Screenshots
			</h3>
			<ul aria-labelledby={id} className={styles.groups}>
				{groups.map((group) => (
					<li key={group.name} className={styles.group}>
						<p className={styles.name}>{group.name}</p>
						<div className={styles.thumbnails}>
							{group.images.map((name) => (
								<Shot
									key={name}
									name={name}
									src={address(item, name)}
									onOpen={() => setOpen(name)}
								/>
							))}
						</div>
					</li>
				))}
			</ul>
			{open === null ? null : (
				<Dialog
					open
					title={open}
					onCancel={() => setOpen(null)}
					className={styles.viewer}
				>
					<div className={styles.frame}>
						<Image name={open} src={address(item, open)} />
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
