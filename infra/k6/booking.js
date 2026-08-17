import http from "k6/http";
import { check, sleep } from "k6";
import { Trend } from "k6/metrics";
import { execution } from "k6/execution";
import { authorizationHeaders, endpoint, profile, requireBenchmarkTarget, requireValue, summaryLine } from "./lib.js";

const fixture = JSON.parse(open(__ENV.BOOKING_FIXTURE || "./artifacts/benchmark-k6-fixture.json"));
const booking = new Trend("booking", true);
export const options = { vus: profile.vus, duration: profile.duration, thresholds: { booking: ["p(95)<1000"] } };

export default function () {
  requireBenchmarkTarget();
  const runId = requireValue("RUN_ID");
  const workerIndex = execution.vu.idInTest - 1;
  const provider = fixture.providers[workerIndex % fixture.providers.length];
  const providerLane = Math.floor(workerIndex / fixture.providers.length);
  const maxWorkersPerProvider = 5; // 50 VUs / 10 deterministic providers.
  if (execution.vu.iterationInInstance >= Math.floor(provider.slotIds.length / maxWorkersPerProvider)) {
    sleep(1); // Do not wrap into an already-consumed slot during the fixed 60 s window.
    return;
  }
  const slot = provider.slotIds[execution.vu.iterationInInstance * maxWorkersPerProvider + providerLane];
  const started = Date.now();
  const response = http.post(endpoint("/api/appointments"), JSON.stringify({ slotId: slot, serviceId: provider.serviceId, idempotencyKey: `k6-${runId}-${execution.vu.idInTest}-${execution.vu.iterationInInstance}` }), { headers: { ...authorizationHeaders, "content-type": "application/json" }, tags: { endpoint: "booking" } });
  check(response, { "booking returned 201": value => value.status === 201 });
  booking.add(Date.now() - started);
}

export function handleSummary(data) { return { stdout: summaryLine("booking", data, ["booking"]) }; }
