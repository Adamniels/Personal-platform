import { Button, useToast } from "../../src";
import { Note, Row, Section } from "./section";

/** Raises a success and an error, each with its action. */
export function ToastSection() {
	const toast = useToast();

	return (
		<Section title="Toast">
			<Row label="Success and error">
				<Button
					onClick={() =>
						toast.success("Entry saved", {
							label: "Undo",
							onAction: () => toast.success("Entry restored"),
						})
					}
				>
					Show a success
				</Button>
				<Button
					onClick={() =>
						toast.error("could not reach the memory service", {
							label: "Retry",
							onAction: () => undefined,
						})
					}
				>
					Show an error
				</Button>
				<Button onClick={() => toast.error("the file is too large")}>
					Show an error without an action
				</Button>
				<Button
					onClick={() =>
						toast.success(
							"Twelve entries moved to the topic Running, and their links to the old topic were kept",
							{ label: "Undo", onAction: () => undefined },
						)
					}
				>
					Show a success that wraps
				</Button>
			</Row>
			<Note>
				A success leaves after 5 seconds; an error stays until its action or
				Dismiss. At most three show, the newest lowest.
			</Note>
		</Section>
	);
}
