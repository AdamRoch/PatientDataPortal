import { proxyProviderSchedule } from "../../proxy";
export async function PUT(request: Request, context: RouteContext<"/api/provider/schedule/services/[id]">) {
  const { id } = await context.params;
  return proxyProviderSchedule(request, `/services/${id}`);
}
export async function DELETE(request: Request, context: RouteContext<"/api/provider/schedule/services/[id]">) {
  const { id } = await context.params;
  return proxyProviderSchedule(request, `/services/${id}`);
}
