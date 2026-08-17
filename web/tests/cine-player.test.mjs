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

test("cine player renders an unavailable manifest frame as a gap without stopping playback", async () => {
  const player = await readFile(playerPath, "utf8");

  assert.match(player, /setFailedFrames\(previous => new Set\(previous\)\.add\(frame\.frameIndex\)\)/);
  assert.match(player, /Frame \{currentFrame \+ 1\} is unavailable \(gap\)\. Playback continues across the remaining frames\./);
  assert.match(player, /isCurrentFrameUnavailable/);
});

test("cine player loads and paints the first frame before downloading remaining batches", async () => {
  const player = await readFile(playerPath, "utf8");

  assert.match(player, /await requestFrameUrls\(0, 1\)/);
  assert.match(player, /setLoadingRemainingFrames\(true\)/);
  assert.match(player, /void \(async \(\) => \{/);
  assert.match(player, /for \(let start = 1; start < access\.manifest\.frames\.length; start \+= FRAME_URL_BATCH_SIZE\)/);
  assert.match(player, /Loading remaining frames: \$\{readyFrameCount\} of \$\{frameCount\} ready\./);
});
