import { proxyProviderSchedule } from "./proxy";
export const GET = (request: Request) => proxyProviderSchedule(request);
