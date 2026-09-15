import type { ExtensionAPI, ExtensionContext } from "@oh-my-pi/pi-coding-agent";

const safe = (value: string | undefined) =>
	value && value.length <= 512 && /^[A-Za-z0-9._/:@+\[\]-]+$/.test(value) ? value : "unknown";

export default function (pi: ExtensionAPI) {
	let context: ExtensionContext | undefined;
	const publish = () => context?.ui.setStatus(
		"flowlocal",
		`FlowLocal OMP: ${safe(context.model?.id)} / ${safe(pi.getThinkingLevel())}`,
	);

	pi.on("session_start", (_event, ctx) => {
		context = ctx;
		publish();
		setInterval(publish, 1000);
	});
	pi.on("session_switch", (_event, ctx) => {
		context = ctx;
		publish();
	});
}
