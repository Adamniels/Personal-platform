// The few stroke icons the Projects tab draws, drawn here rather than taken from a library, since
// Dev flow has no icon dependency and these are three. Each is 20px by default, in the text colour
// it sits in, and hidden from assistive technology: what it stands for is always said in words.
import type { ComponentProps } from "react";

type IconProps = Omit<ComponentProps<"svg">, "children"> & { size?: number };

function Icon({
	size = 20,
	children,
	...rest
}: IconProps & { children: React.ReactNode }) {
	return (
		<svg
			width={size}
			height={size}
			viewBox="0 0 24 24"
			fill="none"
			stroke="currentColor"
			strokeWidth={2}
			strokeLinecap="round"
			strokeLinejoin="round"
			aria-hidden="true"
			focusable="false"
			{...rest}
		>
			{children}
		</svg>
	);
}

/** A branch, for GitHub, whose own mark is a brand's and not a stroke icon. */
export function BranchIcon(props: IconProps) {
	return (
		<Icon {...props}>
			<circle cx="6" cy="6" r="2.5" />
			<circle cx="6" cy="18" r="2.5" />
			<circle cx="18" cy="6" r="2.5" />
			<path d="M6 8.5v7" />
			<path d="M18 8.5a7 7 0 0 1-7 7H8.5" />
		</Icon>
	);
}

/** A key, for the Claude login. */
export function KeyIcon(props: IconProps) {
	return (
		<Icon {...props}>
			<circle cx="7.5" cy="15.5" r="4.5" />
			<path d="M10.7 12.3 20 3" />
			<path d="m16 7 3 3" />
			<path d="m18.5 4.5 2 2" />
		</Icon>
	);
}

/** An arrow up and to the right, for the arrow link that opens a page. */
export function ArrowUpRightIcon(props: IconProps) {
	return (
		<Icon {...props}>
			<path d="M7 17 17 7" />
			<path d="M8 7h9v9" />
		</Icon>
	);
}
