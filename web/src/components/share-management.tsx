"use client";

import { useEffect, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./share-management.module.css";

type ManagedShare = { id: string; resourceType: "image" | "report"; resourceId: string; recipientEmail: string; expiresAt: string; createdAt: string; revokedAt: string | null; status: "active" | "expired" | "revoked" };

export function ShareManagement() {
  const [shares, setShares] = useState<ManagedShare[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<string | null>(null);

  async function refresh() {
    setError(null);
    try { setShares(await request<ManagedShare[]>("/api/patient/shares")); }
    catch { setError("We could not load your shared links. Please try again."); }
  }

  useEffect(() => {
    void request<ManagedShare[]>("/api/patient/shares")
      .then((loaded) => { setShares(loaded); setError(null); })
      .catch(() => setError("We could not load your shared links. Please try again."));
  }, []);

  async function revoke(share: ManagedShare) {
    if (!window.confirm(`Revoke the link shared with ${share.recipientEmail}? It will stop working immediately.`)) return;
    setRevoking(share.id); setError(null);
    try {
      await request<void>(`/api/patient/shares/${encodeURIComponent(share.id)}`, "DELETE");
      await refresh();
    } catch {
      setError("We could not revoke that link. Please try again.");
    } finally { setRevoking(null); }
  }

  if (!shares && !error) return <p aria-busy="true">Loading shared links…</p>;
  return <section className={styles.section} aria-labelledby="share-management-title">
    <div className={styles.heading}><div><h2 id="share-management-title">Shared links</h2><p>Manage secure links you have sent. Revoking a link stops access right away.</p></div><button onClick={() => void refresh()} type="button">Refresh</button></div>
    {error && <p className={styles.error} role="alert">{error}</p>}
    {shares?.length === 0 && <p>You have not shared any images or reports yet.</p>}
    {shares && shares.length > 0 && <ul className={styles.list} aria-label="Your shared links">
      {shares.map((share) => <li key={share.id}>
        <div className={styles.details}><strong>{share.resourceType === "image" ? "Image" : "Report"}</strong><span>Sent to {share.recipientEmail}</span><time dateTime={share.expiresAt}>Expires {formatDate(share.expiresAt)}</time></div>
        <div className={styles.actions}><span className={`${styles.status} ${styles[share.status]}`}>{share.status}</span>{share.status === "active" && <button disabled={revoking === share.id} onClick={() => void revoke(share)} type="button">{revoking === share.id ? "Revoking…" : "Revoke link"}</button>}</div>
      </li>)}
    </ul>}
  </section>;
}

function formatDate(value: string) { return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)); }

async function request<T>(url: string, method = "GET"): Promise<T> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  if (!data.session?.access_token) throw new Error("session_ended");
  const response = await fetch(url, { method, headers: { authorization: `Bearer ${data.session.access_token}` }, cache: "no-store" });
  if (!response.ok) throw new Error("share_request_failed");
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
