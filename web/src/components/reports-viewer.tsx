"use client";

import { type FormEvent, useEffect, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./reports-viewer.module.css";

type SignedReport = { id: string; signedAt: string; studyDescription: string };

export function ReportsViewer() {
  const [reports, setReports] = useState<SignedReport[] | null>(null);
  const [error, setError] = useState<"list" | "view" | null>(null);
  const [loadingReportId, setLoadingReportId] = useState<string | null>(null);
  const [sharingReportId, setSharingReportId] = useState<string | null>(null);
  const [recipientEmail, setRecipientEmail] = useState("");
  const [shareState, setShareState] = useState<"idle" | "sending" | "sent" | "error">("idle");

  useEffect(() => { void requestReports().then(setReports).catch(() => setError("list")); }, []);

  async function openReport(reportId: string) {
    setError(null);
    setLoadingReportId(reportId);
    try {
      const url = await requestReportUrl(reportId);
      window.location.assign(url);
    } catch {
      setError("view");
    } finally {
      setLoadingReportId(null);
    }
  }

  async function shareReport(event: FormEvent<HTMLFormElement>, reportId: string) {
    event.preventDefault();
    setShareState("sending");
    try {
      const response = await fetch(`/api/patient/reports/${encodeURIComponent(reportId)}/share`, {
        method: "POST",
        headers: { authorization: `Bearer ${await token()}`, "content-type": "application/json" },
        body: JSON.stringify({ recipientEmail }),
        cache: "no-store",
      });
      if (!response.ok) throw new Error("share_unavailable");
      setShareState("sent");
      setRecipientEmail("");
    } catch {
      setShareState("error");
    }
  }

  if (error === "list") return <p role="alert">We could not load your reports. Please try again later.</p>;
  if (!reports) return <p aria-busy="true">Loading your signed reports…</p>;
  if (reports.length === 0) return <p>No signed reports are available yet.</p>;

  return <section className={styles.section} aria-label="Signed reports">
    <ul className={styles.list}>
      {reports.map((report) => <li key={report.id}>
        <div>
          <strong>{report.studyDescription}</strong>
          <time dateTime={report.signedAt}>Signed {new Intl.DateTimeFormat(undefined, { dateStyle: "long" }).format(new Date(report.signedAt))}</time>
        </div>
        <div className={styles.actions}>
          <button disabled={loadingReportId === report.id} onClick={() => void openReport(report.id)} type="button">
            {loadingReportId === report.id ? "Opening…" : "View PDF"}
          </button>
          <button aria-expanded={sharingReportId === report.id} onClick={() => { setSharingReportId(sharingReportId === report.id ? null : report.id); setShareState("idle"); }} type="button">Share report</button>
        </div>
        {sharingReportId === report.id && <form className={styles.shareForm} onSubmit={(event) => void shareReport(event, report.id)}>
          <label htmlFor={`share-${report.id}`}>Recipient email</label>
          <input autoComplete="email" id={`share-${report.id}`} onChange={(event) => setRecipientEmail(event.target.value)} required type="email" value={recipientEmail} />
          <button disabled={shareState === "sending"} type="submit">{shareState === "sending" ? "Sending…" : "Send secure link"}</button>
          {shareState === "sent" && <p role="status">Secure link sent. It expires in 48 hours.</p>}
          {shareState === "error" && <p role="alert">We could not share that report. Please try again.</p>}
        </form>}
      </li>)}
    </ul>
    {error === "view" && <p role="alert">We could not open that report. Please try again later.</p>}
  </section>;
}

async function token(): Promise<string> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  if (!data.session?.access_token) throw new Error("session_ended");
  return data.session.access_token;
}

async function requestReports(): Promise<SignedReport[]> {
  const response = await fetch("/api/patient/reports", { headers: { authorization: `Bearer ${await token()}` } });
  if (!response.ok) throw new Error("reports_unavailable");
  return response.json() as Promise<SignedReport[]>;
}

async function requestReportUrl(reportId: string): Promise<string> {
  const response = await fetch(`/api/patient/reports/${encodeURIComponent(reportId)}/view`, { headers: { authorization: `Bearer ${await token()}` } });
  if (!response.ok) throw new Error("report_unavailable");
  const body = await response.json() as { url?: string };
  if (!body.url) throw new Error("report_url_missing");
  return body.url;
}
