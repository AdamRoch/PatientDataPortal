"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./cine-player.module.css";

type Manifest = { frames: unknown[]; defaultFps?: number };
type CineAccess = { id: string; manifest: Manifest };
type FrameUrl = { frameIndex: number; url: string };
type FrameBatch = { frames: FrameUrl[] };

const FRAME_URL_BATCH_SIZE = 50;
const PRELOAD_CONCURRENCY = 4;
const FPS_OPTIONS = [10, 12, 15];

function playbackFps(value: number | undefined) {
  return FPS_OPTIONS.includes(value ?? 0) ? value! : 12;
}

export function CinePlayer({ clipId }: { clipId: string }) {
  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [frameUrls, setFrameUrls] = useState<Record<number, string>>({});
  const [loadedFrames, setLoadedFrames] = useState<Set<number>>(() => new Set());
  const [failedFrames, setFailedFrames] = useState<Set<number>>(() => new Set());
  const [currentFrame, setCurrentFrame] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [fps, setFps] = useState(12);
  const [error, setError] = useState<string | null>(null);
  const [portrait, setPortrait] = useState(true);
  const [loadingRemainingFrames, setLoadingRemainingFrames] = useState(false);
  const preloadQueue = useRef<FrameUrl[]>([]);
  const preloading = useRef(0);
  const preloaded = useRef(new Set<number>());
  const pumpPreloadQueueRef = useRef<() => void>(() => {});

  const pumpPreloadQueue = useCallback(() => {
    while (preloading.current < PRELOAD_CONCURRENCY && preloadQueue.current.length > 0) {
      const frame = preloadQueue.current.shift();
      if (!frame || preloaded.current.has(frame.frameIndex)) continue;
      preloaded.current.add(frame.frameIndex);
      preloading.current += 1;
      const image = new Image();
      const complete = () => {
        preloading.current -= 1;
        if (image.complete && image.naturalWidth > 0) {
          setLoadedFrames(previous => new Set(previous).add(frame.frameIndex));
        } else {
          setFailedFrames(previous => new Set(previous).add(frame.frameIndex));
        }
        pumpPreloadQueueRef.current();
      };
      image.onload = complete;
      image.onerror = complete;
      image.src = frame.url;
    }
  }, []);

  useEffect(() => { pumpPreloadQueueRef.current = pumpPreloadQueue; }, [pumpPreloadQueue]);

  const requestFrameUrls = useCallback(async (startFrame: number, count: number) => {
    const { data } = await getSupabaseBrowserClient().auth.getSession();
    const token = data.session?.access_token;
    if (!token) throw new Error("session_ended");
    const response = await fetch(`/api/patient/cine/${encodeURIComponent(clipId)}/frame-urls`, {
      method: "POST",
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
      body: JSON.stringify({ startFrame, count }),
      cache: "no-store",
    });
    if (!response.ok) throw new Error("frame_urls_unavailable");
    const batch = await response.json() as FrameBatch;
    setFrameUrls(previous => Object.assign({}, previous, ...batch.frames.map(frame => ({ [frame.frameIndex]: frame.url }))));
    preloadQueue.current.push(...batch.frames);
    pumpPreloadQueue();
  }, [clipId, pumpPreloadQueue]);

  useEffect(() => {
    let active = true;
    const load = async () => {
      const { data } = await getSupabaseBrowserClient().auth.getSession();
      const token = data.session?.access_token;
      if (!token) throw new Error("session_ended");
      const response = await fetch(`/api/patient/cine/${encodeURIComponent(clipId)}`, { headers: { authorization: `Bearer ${token}` }, cache: "no-store" });
      if (!response.ok) throw new Error("cine_unavailable");
      const access = await response.json() as CineAccess;
      if (!active) return;
      setManifest(access.manifest);
      setFps(playbackFps(access.manifest.defaultFps));
      if (access.manifest.frames.length === 0) return;

      // The first frame establishes useful visual feedback before the potentially slow
      // remainder of a large clip starts downloading.
      await requestFrameUrls(0, 1);
      if (!active) return;

      setLoadingRemainingFrames(true);
      void (async () => {
        try {
          for (let start = 1; start < access.manifest.frames.length; start += FRAME_URL_BATCH_SIZE) {
            await requestFrameUrls(start, Math.min(FRAME_URL_BATCH_SIZE, access.manifest.frames.length - start));
            if (!active) return;
          }
        } catch {
          if (active) setError("We could not load this cine clip. Please try again later.");
        } finally {
          if (active) setLoadingRemainingFrames(false);
        }
      })();
    };
    void load().catch(() => { if (active) setError("We could not load this cine clip. Please try again later."); });
    return () => { active = false; };
  }, [clipId, requestFrameUrls]);

  useEffect(() => {
    const updateOrientation = () => setPortrait(window.matchMedia("(orientation: portrait)").matches);
    updateOrientation();
    window.addEventListener("orientationchange", updateOrientation);
    return () => window.removeEventListener("orientationchange", updateOrientation);
  }, []);

  const frameCount = manifest?.frames.length ?? 0;
  useEffect(() => {
    if (!playing || frameCount < 2) return;
    const timer = window.setTimeout(() => setCurrentFrame(frame => frame >= frameCount - 1 ? 0 : frame + 1), 1000 / fps);
    return () => window.clearTimeout(timer);
  }, [playing, fps, currentFrame, frameCount]);

  if (error) return <p className={styles.error} role="alert">{error}</p>;
  if (!manifest) return <p aria-busy="true">Loading cine clip…</p>;
  if (frameCount === 0) return <p role="alert">This cine clip has no frames.</p>;

  const currentUrl = frameUrls[currentFrame];
  const isCurrentFrameLoaded = loadedFrames.has(currentFrame);
  const isCurrentFrameUnavailable = failedFrames.has(currentFrame);
  const readyFrameCount = loadedFrames.size + failedFrames.size;
  const step = (direction: number) => setCurrentFrame(frame => Math.max(0, Math.min(frameCount - 1, frame + direction)));

  return <section className={styles.player} aria-label="Cine player" data-orientation={portrait ? "portrait" : "landscape"}>
    <div className={styles.canvas} aria-busy={!isCurrentFrameLoaded && !isCurrentFrameUnavailable}>
      {currentUrl && isCurrentFrameLoaded
        // Signed storage URLs are minted at runtime and must not pass through an image optimization proxy.
        // eslint-disable-next-line @next/next/no-img-element
        ? <img className={styles.frame} src={currentUrl} alt={`Cine frame ${currentFrame + 1} of ${frameCount}`} />
        : isCurrentFrameUnavailable
          ? <p className={styles.gap} role="status">Frame {currentFrame + 1} is unavailable (gap). Playback continues across the remaining frames.</p>
          : <p>Loading frame {currentFrame + 1}…</p>}
    </div>
    <div className={styles.controls} aria-label="Cine playback controls">
      <button type="button" aria-label="Previous frame" onClick={() => step(-1)} disabled={currentFrame === 0}>Previous</button>
      <button type="button" aria-label={playing ? "Pause playback" : "Play playback"} onClick={() => setPlaying(value => !value)}>{playing ? "Pause" : "Play"}</button>
      <button type="button" aria-label="Next frame" onClick={() => step(1)} disabled={currentFrame === frameCount - 1}>Next</button>
      <label className={styles.scrubberLabel}>Frame {currentFrame + 1} of {frameCount}
        <input aria-label="Cine frame position" type="range" min="0" max={frameCount - 1} value={currentFrame} onChange={event => setCurrentFrame(Number(event.target.value))} />
      </label>
      <label className={styles.fpsLabel}>Playback speed
        <select aria-label="Frames per second" value={fps} onChange={event => setFps(Number(event.target.value))}>
          {FPS_OPTIONS.map(option => <option key={option} value={option}>{option} FPS</option>)}
        </select>
      </label>
    </div>
    <p className={styles.loading} role="status" aria-live="polite">
      {loadingRemainingFrames
        ? `Loading remaining frames: ${readyFrameCount} of ${frameCount} ready.`
        : readyFrameCount < frameCount
          ? `Loading frame ${currentFrame + 1} of ${frameCount}…`
          : `All ${frameCount} frames are ready.`}
    </p>
  </section>;
}
