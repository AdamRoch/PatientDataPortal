import http from "k6/http";
import { check } from "k6";
import { Counter, Trend } from "k6/metrics";
import { authorizationHeaders, endpoint, profile, requireBenchmarkTarget, requireValue, summaryLine } from "./lib.js";

const cineFullLoad = new Trend("cine_full_load", true);
const cineStorageBytes = new Counter("cine_storage_bytes");
const frameCount = Number(__ENV.CINE_FRAME_COUNT || 100);
const frameBytes = Number(__ENV.CINE_FRAME_BYTES || 40960);
const monthlyCapBytes = 5 * 1024 * 1024 * 1024;
export const options = { vus: profile.vus, duration: profile.duration, thresholds: { cine_full_load: ["p(95)<5000"] } };

export default function () {
  requireBenchmarkTarget();
  const started = Date.now();
  const clipId = requireValue("CINE_ID");
  const manifest = http.get(endpoint(`/api/cine/${clipId}`), { headers: authorizationHeaders, tags: { endpoint: "cine_manifest" } });
  check(manifest, { "cine manifest returned 200": response => response.status === 200 });
  if (manifest.status !== 200) return;

  for (let start = 0; start < frameCount; start += 50) {
    const urls = http.post(endpoint(`/api/cine/${clipId}/frame-urls`), JSON.stringify({ startFrame: start, count: Math.min(50, frameCount - start) }), { headers: { ...authorizationHeaders, "content-type": "application/json" }, tags: { endpoint: "cine_frame_urls" } });
    check(urls, { "frame URL batch returned 200": response => response.status === 200 });
    if (urls.status !== 200) return;
    const frames = urls.json("frames") || [];
    const responses = http.batch(frames.map(frame => ({ method: "GET", url: frame.url, tags: { endpoint: "cine_frame_storage" } })));
    for (const response of responses) {
      check(response, { "cine frame bytes returned 200": value => value.status === 200 });
      cineStorageBytes.add(Number(response.headers["Content-Length"] || 0));
    }
  }
  cineFullLoad.add(Date.now() - started);
}

export function handleSummary(data) {
  const iterations = data.metrics.iterations?.values?.count || 0;
  const estimate = iterations * frameCount * frameBytes;
  const percent = (estimate / monthlyCapBytes) * 100;
  return { stdout: `${summaryLine("cine-full-load", data, ["cine_full_load"])}cine-egress-estimate bytes=${estimate} mib=${(estimate / 1024 / 1024).toFixed(2)} monthly_cap_mib=5120 cap_percent=${percent.toFixed(3)} model=${frameCount}x${frameBytes}B\n` };
}
