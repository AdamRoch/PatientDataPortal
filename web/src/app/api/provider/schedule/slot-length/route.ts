import { proxyProviderSchedule } from "../proxy";
export const PUT = (request: Request) => proxyProviderSchedule(request, "/slot-length");
