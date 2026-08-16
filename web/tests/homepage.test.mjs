import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("homepage exports the application entry point", async () => {
  const page = await readFile(new URL("../src/app/page.tsx", import.meta.url), "utf8");

  assert.match(page, /export default function Home/);
});
