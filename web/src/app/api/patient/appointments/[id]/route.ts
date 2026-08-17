import { proxyAppointments } from "../route";

export async function DELETE(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  return proxyAppointments(request, `/${encodeURIComponent(id)}`);
}
