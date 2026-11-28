// The "Machines" tab: the owner's machines that run the machine program, each online or since when
// it has not been seen, with its OS, architecture, program version and Docker's state, and
// "Remove", which asks first. "Add a machine" makes a one-time code and shows the two commands
// that install the program with it, for any machine and for the machine the platform runs on. The
// list is GET /devflow/api/machines, read again every ten seconds while the tab is open, so a
// machine that enrols, or goes quiet, shows without a reload. See "The pages" and "Machines" in
// devflow/README.md.
import { Button, Card, Dialog, EmptyState, Status } from "@platform/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import type { components } from "./api/schema";
import styles from "./machines.module.css";
import { Command } from "./project";

export type Machine = components["schemas"]["Machine"];
export type MachineEnrollment = components["schemas"]["MachineEnrollment"];

/** How the tab reads, adds and removes machines, Dev flow's API in the page
 * and a test's own in a test. */
export type MachinesApi = {
	loadMachines: () => Promise<Machine[]>;
	newEnrollment: () => Promise<MachineEnrollment>;
	removeMachine: (id: string) => Promise<void>;
};

export const MACHINES = ["devflow", "machines"] as const;

/** How often the list is read again while the tab is open. */
const EVERY_MS = 10_000;

export const NO_MACHINE = "No machine yet";
export const NO_MACHINE_DETAIL =
	"Add a machine, and it shows here once the program on it has enrolled.";
export const NOT_READ = "The machines could not be read. Try again.";
export const NO_CODE = "No code was made. Try again.";
export const NOT_REMOVED = "The machine was not removed. Try again.";
export const RUN_ONE =
	"Run one of these in a terminal on the machine. Its code enrols one machine, once.";

const OS: Record<string, string> = { macos: "macOS", linux: "Linux" };

const TIME = new Intl.DateTimeFormat("en-GB", { timeStyle: "short" });
const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

export type MachinesProps = MachinesApi & {
	/** Copies text to the clipboard, the browser's by default. */
	copy?: (text: string) => Promise<void>;
};

const clipboard = (text: string) => navigator.clipboard.writeText(text);

export function Machines({
	loadMachines,
	newEnrollment,
	removeMachine,
	copy = clipboard,
}: MachinesProps) {
	const queries = useQueryClient();
	const read = useQuery({
		queryKey: MACHINES,
		queryFn: loadMachines,
		refetchInterval: EVERY_MS,
		refetchOnWindowFocus: true,
	});
	const [enrollment, setEnrollment] = useState<MachineEnrollment | null>(null);
	const [adding, setAdding] = useState(false);
	const [failed, setFailed] = useState<string | null>(null);
	const [removing, setRemoving] = useState<Machine | null>(null);

	const add = async () => {
		setAdding(true);
		try {
			setEnrollment(await newEnrollment());
			setFailed(null);
		} catch {
			setFailed(NO_CODE);
		}
		setAdding(false);
	};

	const remove = async (machine: Machine) => {
		try {
			await removeMachine(machine.id);
			setFailed(null);
		} catch {
			setFailed(NOT_REMOVED);
		}
		setRemoving(null);
		await queries.invalidateQueries({ queryKey: MACHINES });
	};

	const machines = read.data;
	return (
		<section className={styles.machines} aria-label="Machines">
			<div className={styles.bar}>
				<Button kind="primary" disabled={adding} onClick={add}>
					Add a machine
				</Button>
			</div>
			{failed === null ? null : (
				<p className={styles.error} role="alert">
					{failed}
				</p>
			)}
			{read.isError ? (
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			) : machines === undefined ? null : machines.length === 0 ? (
				<EmptyState title={NO_MACHINE} detail={NO_MACHINE_DETAIL} />
			) : (
				<Card
					title="Machines"
					aside={<Online machines={machines} />}
					className={styles.card}
				>
					<ul className={styles.list} aria-label="Machines">
						{machines.map((machine) => (
							<MachineRow
								key={machine.id}
								machine={machine}
								remove={() => setRemoving(machine)}
							/>
						))}
					</ul>
				</Card>
			)}
			<Dialog
				open={enrollment !== null}
				title="Add a machine"
				onCancel={() => {
					setEnrollment(null);
					void queries.invalidateQueries({ queryKey: MACHINES });
				}}
			>
				{enrollment === null ? null : (
					<div className={styles.commands}>
						<p className={styles.line}>{RUN_ONE}</p>
						<Command
							label="On any machine"
							copyLabel="Copy the command"
							text={enrollment.command}
							copy={copy}
						/>
						<Command
							label="On the machine the platform runs on"
							copyLabel="Copy the local command"
							text={enrollment.local_command}
							copy={copy}
						/>
						<p className={styles.line}>
							The code expires at{" "}
							<time className={styles.mono} dateTime={enrollment.expires_at}>
								{TIME.format(new Date(enrollment.expires_at))}
							</time>
							.
						</p>
					</div>
				)}
			</Dialog>
			<Dialog
				open={removing !== null}
				kind="destructive"
				title={`Remove ${removing?.name ?? "the machine"}?`}
				confirmLabel="Remove"
				onConfirm={() => (removing ? remove(removing) : undefined)}
				onCancel={() => setRemoving(null)}
			>
				Its key stops working at once. The program on it reaches Dev flow again
				only once the machine is added again.
			</Dialog>
		</section>
	);
}

/** How many of the machines are online, as the card's caption. */
function Online({ machines }: { machines: Machine[] }) {
	const online = machines.filter((machine) => machine.online).length;
	return (
		<span className={styles.caption}>
			<span className={styles.mono}>{online}</span> of{" "}
			<span className={styles.mono}>{machines.length}</span> online
		</span>
	);
}

/** A machine as an inset row: its name and Remove; online, or since when it
 * has not been seen; and what it runs, or that it has not said hello yet. */
function MachineRow({
	machine,
	remove,
}: {
	machine: Machine;
	remove: () => void;
}) {
	return (
		<li className={styles.machine}>
			<div className={styles.head}>
				<span className={styles.name}>{machine.name}</span>
				<Button
					kind="ghost"
					onClick={remove}
					aria-label={`Remove ${machine.name}`}
				>
					Remove
				</Button>
			</div>
			<span className={styles.state}>
				{machine.online ? (
					<Status tone="running">Online</Status>
				) : (
					<>
						<Status tone="idle">Offline</Status>
						<span className={styles.quiet}>
							Not seen since{" "}
							<time className={styles.mono} dateTime={machine.last_seen_at}>
								{DATE_TIME.format(new Date(machine.last_seen_at))}
							</time>
						</span>
					</>
				)}
			</span>
			<span className={styles.meta}>
				{OS[machine.os] ?? machine.os} ·{" "}
				<span className={styles.mono}>{machine.arch}</span>
				{machine.program_version === null ? (
					<> · Not said hello yet</>
				) : (
					<>
						{" "}
						· Program{" "}
						<span className={styles.mono}>{machine.program_version}</span>
					</>
				)}
			</span>
			{machine.docker === null ? null : machine.docker === "running" ? (
				<span className={styles.meta}>Docker running</span>
			) : (
				<span className={styles.docker}>
					<Status tone="needs-you">No Docker</Status>
					<span>{machine.docker}</span>
				</span>
			)}
		</li>
	);
}
