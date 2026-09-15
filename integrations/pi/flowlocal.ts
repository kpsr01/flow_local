import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";

const safe = (value: string | undefined) =>
	value && value.length <= 512 && /^[A-Za-z0-9._/:@+\[\]-]+$/.test(value) ? value : "unknown";

export default function (pi: ExtensionAPI) {
	const publish = (ctx: ExtensionContext, model = ctx.model?.id, reasoning = pi.getThinkingLevel()) =>
		ctx.ui.setStatus("flowlocal", `FlowLocal Pi: ${safe(model)} / ${safe(reasoning)}`);

	pi.on("session_start", (_event, ctx) => publish(ctx));
	pi.on("model_select", (event, ctx) => publish(ctx, event.model.id));
	pi.on("thinking_level_select", (event, ctx) => publish(ctx, undefined, event.level));
}
