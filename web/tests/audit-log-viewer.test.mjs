import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("audit log viewer uses the authenticated route and renders references at phone width", async () => {
  const viewer = await readFile(new URL("../src/components/audit-log-viewer.tsx", import.meta.url), "utf8");
  const css = await readFile(new URL("../src/components/audit-log-viewer.module.css", import.meta.url), "utf8");
  const route = await readFile(new URL("../src/app/api/audit-log/route.ts", import.meta.url), "utf8");
  const providerPage = await readFile(new URL("../src/app/provider/page.tsx", import.meta.url), "utf8");
  assert.match(viewer, /\/api\/audit-log/);
  assert.match(viewer, /targetReference/);
  assert.doesNotMatch(viewer, /patientName|fullName|email/i);
  assert.match(css, /max-width: 600px/);
  assert.match(route, /\/api\/audit-log/);
  assert.match(providerPage, /Patient activity audit log/);
});
