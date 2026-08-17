import { proxyProviderSchedule } from "../proxy";
export const POST = (request: Request) => proxyProviderSchedule(request, "/blocked-times");
