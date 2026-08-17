import { fail } from "k6";

export const profile = {
  vus: Number(__ENV.VUS || 20),
  duration: __ENV.DURATION || "60s",
};

export const authorizationHeaders = {
  authorization: `Bearer ${__ENV.PATIENT_ACCESS_TOKEN || ""}`,
};

export function requireBenchmarkTarget() {
  if (__ENV.ALLOW_BENCHMARK_TARGET !== "1") {
    fail("Set ALLOW_BENCHMARK_TARGET=1 only for an approved synthetic benchmark target.");
  }
  if (!__ENV.BASE_URL || !__ENV.PATIENT_ACCESS_TOKEN) {
    fail("BASE_URL and PATIENT_ACCESS_TOKEN are required.");
  }
  if (profile.vus < 20 || profile.vus > 50 || profile.duration !== "60s") {
    fail("This harness is deliberately limited to 20-50 VUs for exactly 60s.");
  }
}

export function endpoint(path) {
  return `${__ENV.BASE_URL.replace(/\/$/, "")}${path}`;
}

export function requireValue(name) {
  const value = __ENV[name];
  if (!value) fail(`${name} is required.`);
  return value;
}

export function p95Summary(data, metric) {
  const values = data.metrics[metric]?.values;
  return values ? values["p(95)"] : null;
}

export function summaryLine(name, data, metrics) {
  const fields = metrics.map(metric => `${metric}_p95_ms=${p95Summary(data, metric) ?? "missing"}`);
  return `${name} iterations=${data.metrics.iterations?.values?.count ?? 0} ${fields.join(" ")}\n`;
}
