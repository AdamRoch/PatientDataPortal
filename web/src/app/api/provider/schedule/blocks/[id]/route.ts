import { proxyProviderSchedule } from "../../proxy";
export async function PUT(request: Request, context: RouteContext<"/api/provider/schedule/blocks/[id]">) {
  const { id } = await context.params;
  return proxyProviderSchedule(request, `/blocked-times/${id}`);
}
export async function DELETE(request: Request, context: RouteContext<"/api/provider/schedule/blocks/[id]">) {
  const { id } = await context.params;
  return proxyProviderSchedule(request, `/blocked-times/${id}`);
}
