"use client";

import { useEffect, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./email-outbox-viewer.module.css";

type EmailOutboxStatus = {
  kind: string;
  status: string;
  attempts: number;
  dueAt: string;
  sentAt: string | null;
  providerMessageId: string | null;
};

export function EmailOutboxViewer() {
  const [rows, setRows] = useState<EmailOutboxStatus[] | null>(null);
  const [forbidden, setForbidden] = useState(false);
  const [error, setError] = useState(false);

  useEffect(() => { void requestRows().then(setRows).catch((reason: unknown) => reason === "forbidden" ? setForbidden(true) : setError(true)); }, []);

  if (forbidden) return <p role="alert">You do not have access to email delivery status.</p>;
  if (error) return <p role="alert">We could not load email delivery status. Please try again later.</p>;
  if (!rows) return <p aria-busy="true">Loading email delivery status…</p>;
  if (rows.length === 0) return <p>No email delivery activity is available yet.</p>;

  return <section className={styles.section} aria-label="Email outbox status">
    <ul className={styles.list}>
      {rows.map((row, index) => <li key={`${row.kind}-${row.dueAt}-${index}`}>
        <div className={styles.heading}><strong>{row.kind}</strong><span className={styles.status}>{row.status}</span></div>
        <dl>
          <div><dt>Attempts</dt><dd>{row.attempts}</dd></div>
          <div><dt>Due</dt><dd><time dateTime={row.dueAt}>{formatDate(row.dueAt)}</time></dd></div>
          <div><dt>Sent</dt><dd>{row.sentAt ? <time dateTime={row.sentAt}>{formatDate(row.sentAt)}</time> : "Not sent"}</dd></div>
          <div><dt>Provider message ID</dt><dd>{row.providerMessageId ?? "Not available"}</dd></div>
        </dl>
      </li>)}
    </ul>
  </section>;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

async function requestRows(): Promise<EmailOutboxStatus[]> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  if (!data.session?.access_token) throw new Error("session_ended");
  const response = await fetch("/api/admin/email-outbox", { headers: { authorization: `Bearer ${data.session.access_token}` } });
  if (response.status === 401 || response.status === 403) throw "forbidden";
  if (!response.ok) throw new Error("outbox_unavailable");
  return response.json() as Promise<EmailOutboxStatus[]>;
}
