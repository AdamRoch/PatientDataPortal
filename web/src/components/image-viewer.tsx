"use client";

import { PointerEvent, useCallback, useEffect, useRef, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./image-viewer.module.css";

type ImageAccess = { id: string; studyId: string; signedUrl: string; expiresAt: string };
type Point = { x: number; y: number };
const MIN_ZOOM = 1;
const MAX_ZOOM = 4;

export function ImageViewer({ imageId }: { imageId: string }) {
  const [access, setAccess] = useState<ImageAccess | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [zoom, setZoom] = useState(MIN_ZOOM);
  const [pan, setPan] = useState<Point>({ x: 0, y: 0 });
  const dragStart = useRef<Point | null>(null);
  const hasRemintedAfterLoadError = useRef(false);

  const mint = useCallback(async () => {
    setError(null);
    const { data } = await getSupabaseBrowserClient().auth.getSession();
    const token = data.session?.access_token;
    if (!token) throw new Error("session_ended");
    const response = await fetch(`/api/patient/images/${encodeURIComponent(imageId)}`, { headers: { authorization: `Bearer ${token}` }, cache: "no-store" });
    if (!response.ok) throw new Error("image_unavailable");
    setAccess(await response.json() as ImageAccess);
  }, [imageId]);

  useEffect(() => {
    hasRemintedAfterLoadError.current = false;
    queueMicrotask(() => { void mint().catch(() => setError("We could not load this image. Please try again later.")); });
  }, [mint]);
  const reset = () => { setZoom(MIN_ZOOM); setPan({ x: 0, y: 0 }); };
  const changeZoom = (amount: number) => setZoom(current => Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Number((current + amount).toFixed(2)))));
  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    dragStart.current = { x: event.clientX - pan.x, y: event.clientY - pan.y };
  };
  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    if (!dragStart.current || zoom === MIN_ZOOM) return;
    setPan({ x: event.clientX - dragStart.current.x, y: event.clientY - dragStart.current.y });
  };
  const onPointerEnd = () => { dragStart.current = null; };
  const remintAfterExpiredUrl = () => {
    if (hasRemintedAfterLoadError.current) { setError("This image could not be displayed. Please try again."); return; }
    hasRemintedAfterLoadError.current = true;
    void mint().catch(() => setError("This image link expired and could not be refreshed. Please try again."));
  };

  if (error) return <p className={styles.error} role="alert">{error} <button type="button" onClick={() => { hasRemintedAfterLoadError.current = false; void mint().catch(() => setError("We could not load this image. Please try again later.")); }}>Retry</button></p>;
  if (!access) return <p aria-busy="true">Loading image…</p>;

  return <section className={styles.viewer} aria-label="Image viewer">
    <div className={styles.toolbar} aria-label="Image controls">
      <button type="button" aria-label="Zoom in" onClick={() => changeZoom(.25)}>Zoom in</button>
      <button type="button" aria-label="Zoom out" onClick={() => changeZoom(-.25)}>Zoom out</button>
      <button type="button" aria-label="Reset zoom and pan" onClick={reset}>Reset view</button>
      <span aria-live="polite">{Math.round(zoom * 100)}%</span>
    </div>
    <div className={styles.canvas} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerEnd} onPointerCancel={onPointerEnd}>
      {/* Signed storage URLs are minted at runtime and must not pass through an image optimization proxy. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img className={styles.image} src={access.signedUrl} alt="Your diagnostic image" draggable={false} onError={remintAfterExpiredUrl} style={{ transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})` }} />
    </div>
  </section>;
}
