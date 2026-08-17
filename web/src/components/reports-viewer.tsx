"use client";

import { useEffect, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./reports-viewer.module.css";

type SignedReport = { id: string; signedAt: string; studyDescription: string };
type ViewState = { id: string; url: string } | null;

export function ReportsViewer() {
  const [reports, setReports] = useState<SignedReport[] | null>(null);
  const [view, setView] = useState<ViewState>(null);
  const [error, setError] = useState<"list" | "view" | null>(null);
  const [loadingReportId, setLoadingReportId] = useState<string | null>(null);
  const [pdfLoading, setPdfLoading] = useState(false);

  useEffect(() => { void requestReports().then(setReports).catch(() => setError("list")); }, []);

  async function openReport(reportId: string) {
    setError(null);
    setLoadingReportId(reportId);
    setPdfLoading(false);
    try {
      const url = await requestReportUrl(reportId);
      setPdfLoading(true);
      setView({ id: reportId, url });
    } catch {
      setError("view");
    } finally {
      setLoadingReportId(null);
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
        <button aria-controls="report-viewer" disabled={loadingReportId === report.id} onClick={() => void openReport(report.id)} type="button">
          {loadingReportId === report.id ? "Opening…" : "View PDF"}
        </button>
      </li>)}
    </ul>
    {error === "view" && <p role="alert">We could not open that report. Please try again later.</p>}
    {view && <div className={styles.viewer} id="report-viewer" aria-busy={pdfLoading}>
      {pdfLoading && <p className={styles.loading} role="status">Loading your report…</p>}
      <iframe key={view.url} onError={() => { setPdfLoading(false); setError("view"); }} onLoad={() => setPdfLoading(false)} src={view.url} title="Signed report PDF" />
    </div>}
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
