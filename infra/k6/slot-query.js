import http from "k6/http";
import { check } from "k6";
import { Trend } from "k6/metrics";
import { authorizationHeaders, endpoint, profile, requireBenchmarkTarget, requireValue, summaryLine } from "./lib.js";

const slotQuery = new Trend("slot_query", true);
export const options = { vus: profile.vus, duration: profile.duration, thresholds: { slot_query: ["p(95)<1000"] } };

export default function () {
  requireBenchmarkTarget();
  const started = Date.now();
  const response = http.get(endpoint(`/api/providers/${requireValue("PROVIDER_ID")}/slots?from=2030-01-07T00:00:00Z&to=2030-02-07T00:00:00Z`), { headers: authorizationHeaders, tags: { endpoint: "slot_query" } });
  check(response, { "slot query returned 200": value => value.status === 200 });
  slotQuery.add(Date.now() - started);
}

export function handleSummary(data) { return { stdout: summaryLine("slot-query", data, ["slot_query"]) }; }
