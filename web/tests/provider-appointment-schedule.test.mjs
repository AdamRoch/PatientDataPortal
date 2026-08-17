import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("provider appointment schedule uses scoped routes and lifecycle actions", async () => {
  const component = await readFile(new URL("../src/components/provider-appointment-schedule.tsx", import.meta.url), "utf8");
  const css = await readFile(new URL("../src/components/provider-appointment-schedule.module.css", import.meta.url), "utf8");
  const listRoute = await readFile(new URL("../src/app/api/provider/appointments/route.ts", import.meta.url), "utf8");
  const statusRoute = await readFile(new URL("../src/app/api/provider/appointments/[id]/status/route.ts", import.meta.url), "utf8");
  assert.match(component, /\/api\/provider\/appointments/);
  assert.match(component, /Complete/); assert.match(component, /No-show/); assert.match(component, /Cancel/);
  assert.match(component, /timeZone/); assert.match(css, /max-width: 480px/);
  assert.match(listRoute, /proxyProviderAppointments/);
  assert.match(statusRoute, /\/api\/appointments\/\$\{encodeURIComponent\(id\)\}\/status/);
});
