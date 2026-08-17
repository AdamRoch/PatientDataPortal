import { proxyProviderAppointments } from "./proxy";

export const GET = (request: Request) => proxyProviderAppointments(request);
