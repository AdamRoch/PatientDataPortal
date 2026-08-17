import { proxyProviderAppointments } from "../../proxy";

export async function PATCH(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return proxyProviderAppointments(request, `/api/appointments/${encodeURIComponent(id)}/status`);
}
