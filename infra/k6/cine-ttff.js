import http from "k6/http";
import { check } from "k6";
import { Trend } from "k6/metrics";
import { authorizationHeaders, endpoint, profile, requireBenchmarkTarget, requireValue, summaryLine } from "./lib.js";

const cineTtff = new Trend("cine_ttff", true);
export const options = { vus: profile.vus, duration: profile.duration, thresholds: { cine_ttff: ["p(95)<1000"] } };

export default function () {
  requireBenchmarkTarget();
  const started = Date.now();
  const clipId = requireValue("CINE_ID");
  const manifest = http.get(endpoint(`/api/cine/${clipId}`), { headers: authorizationHeaders, tags: { endpoint: "cine_manifest" } });
  check(manifest, { "cine manifest returned 200": response => response.status === 200 });
  if (manifest.status !== 200) return;
  const urls = http.post(endpoint(`/api/cine/${clipId}/frame-urls`), JSON.stringify({ startFrame: 0, count: 1 }), { headers: { ...authorizationHeaders, "content-type": "application/json" }, tags: { endpoint: "cine_first_url" } });
  check(urls, { "first frame URL returned 200": response => response.status === 200 });
  if (urls.status !== 200) return;
  const frame = urls.json("frames.0.url");
  const response = http.get(frame, { tags: { endpoint: "cine_first_frame_storage" } });
  check(response, { "first frame bytes returned 200": value => value.status === 200 });
  cineTtff.add(Date.now() - started);
}

export function handleSummary(data) { return { stdout: summaryLine("cine-ttff", data, ["cine_ttff"]) }; }
