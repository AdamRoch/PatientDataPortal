import http from "k6/http";
import { check } from "k6";
import { Trend } from "k6/metrics";
import { authorizationHeaders, endpoint, profile, requireBenchmarkTarget, requireValue, summaryLine } from "./lib.js";

const imageLoad = new Trend("image_load", true);
export const options = { vus: profile.vus, duration: profile.duration, thresholds: { image_load: ["p(95)<1000"] } };

export default function () {
  requireBenchmarkTarget();
  const started = Date.now();
  const access = http.get(endpoint(`/api/images/${requireValue("IMAGE_ID")}`), { headers: authorizationHeaders, tags: { endpoint: "image_access" } });
  check(access, { "image access returned 200": response => response.status === 200 });
  if (access.status !== 200) return;
  const signedUrl = access.json("url");
  const image = http.get(signedUrl, { tags: { endpoint: "image_storage" } });
  check(image, { "image bytes returned 200": response => response.status === 200 });
  imageLoad.add(Date.now() - started);
}

export function handleSummary(data) { return { stdout: summaryLine("image-load", data, ["image_load"]) }; }
