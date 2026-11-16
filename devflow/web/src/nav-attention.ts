// Asks the platform's nav bar to ask again whether Dev flow needs the owner, so the dot on Dev
// flow's link follows what he just did rather than what the nav remembered. The nav listens for
// this event on window, forgets what it remembered and asks each attention address again. See
// "The attention endpoint" in High-level/docs/feature-contract.md and "Navigation" in
// High-level/docs/platform-architecture.md.
export const NAV_ATTENTION_EVENT = "platform-nav-attention";

export function askNavAgain(target: EventTarget = window): void {
	target.dispatchEvent(new Event(NAV_ATTENTION_EVENT));
}
