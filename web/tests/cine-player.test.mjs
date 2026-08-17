import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const playerPath = new URL("../src/components/cine-player.tsx", import.meta.url);

test("cine player exposes labeled transport, scrubber, and FPS controls", async () => {
  const player = await readFile(playerPath, "utf8");

  assert.match(player, /aria-label="Previous frame"/);
  assert.match(player, /aria-label="Next frame"/);
  assert.match(player, /aria-label="Cine frame position"/);
  assert.match(player, /aria-label="Frames per second"/);
  assert.match(player, /setPlaying\(value => !value\)/);
});

test("cine player preloads at bounded concurrency and retains state across orientation updates", async () => {
  const player = await readFile(playerPath, "utf8");

  assert.match(player, /const PRELOAD_CONCURRENCY = 4/);
  assert.match(player, /preloading\.current < PRELOAD_CONCURRENCY/);
  assert.match(player, /window\.addEventListener\("orientationchange", updateOrientation\)/);
  assert.match(player, /setCurrentFrame\(frame => frame >= frameCount - 1 \? 0 : frame \+ 1\)/);
});
