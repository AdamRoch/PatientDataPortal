"use client";

import { FormEvent, useEffect, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./audit-log-viewer.module.css";

type AuditLogItem = { actorReference: string | null; actorRole: string; action: string; targetType: string; targetReference: string; result: string; occurredAt: string };
type Filters = { actor: string; action: string; date: string };
const emptyFilters: Filters = { actor: "", action: "", date: "" };

export function AuditLogViewer() {
  const [rows, setRows] = useState<AuditLogItem[] | null>(null);
  const [filters, setFilters] = useState<Filters>(emptyFilters);
  const [error, setError] = useState(false);
  useEffect(() => { void load(emptyFilters).then(setRows).catch(() => setError(true)); }, []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(false); setRows(null);
    try { setRows(await load(filters)); } catch { setError(true); }
  }

  return <section className={styles.section} aria-label="Audit log">
    <form className={styles.filters} onSubmit={submit}>
      <label>Actor reference<input value={filters.actor} onChange={(event) => setFilters({ ...filters, actor: event.target.value })} /></label>
      <label>Action<input value={filters.action} onChange={(event) => setFilters({ ...filters, action: event.target.value })} /></label>
      <label>Date<input type="date" value={filters.date} onChange={(event) => setFilters({ ...filters, date: event.target.value })} /></label>
      <button type="submit">Apply filters</button>
    </form>
    {error ? <p role="alert">We could not load the audit log. Please try again later.</p> : !rows ? <p aria-busy="true">Loading audit log…</p> : !rows.length ? <p>No audit events match these filters.</p> : <ul className={styles.list}>{rows.map((row, index) => <li key={`${row.occurredAt}-${row.targetReference}-${index}`}>
      <div className={styles.heading}><strong>{row.action}</strong><span className={styles.result}>{row.result}</span></div>
      <dl>
        <div><dt>When</dt><dd><time dateTime={row.occurredAt}>{formatDate(row.occurredAt)}</time></dd></div>
        <div><dt>Actor</dt><dd>{row.actorRole}: {row.actorReference ?? "Anonymous"}</dd></div>
        <div><dt>Target</dt><dd>{row.targetType}: {row.targetReference}</dd></div>
      </dl>
    </li>)}</ul>}
  </section>;
}

function formatDate(value: string) { return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)); }
async function load(filters: Filters): Promise<AuditLogItem[]> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  if (!data.session?.access_token) throw new Error("session_ended");
  const params = new URLSearchParams(Object.entries(filters).filter(([, value]) => value));
  const response = await fetch(`/api/audit-log${params.size ? `?${params}` : ""}`, { headers: { authorization: `Bearer ${data.session.access_token}` } });
  if (!response.ok) throw new Error("audit_log_unavailable");
  return response.json() as Promise<AuditLogItem[]>;
}
