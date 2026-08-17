"use client";
import { useEffect, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";

type Request = { id: string; patientReference: string | null; requestedAt: string };
export function DeletionRequestsViewer() {
  const [rows, setRows] = useState<Request[] | null>(null); const [error, setError] = useState(false);
  useEffect(() => { void load().then(setRows).catch(() => setError(true)); }, []);
  if (error) return <p role="alert">You do not have access to deletion requests.</p>;
  if (!rows) return <p aria-busy="true">Loading deletion requests…</p>;
  if (!rows.length) return <p>No deletion requests are waiting for review.</p>;
  return <ul aria-label="Pending deletion requests">{rows.map((row) => <li key={row.id}><strong>{row.patientReference ?? "Unlinked patient"}</strong><time dateTime={row.requestedAt}> Requested {new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(row.requestedAt))}</time></li>)}</ul>;
}
async function load(): Promise<Request[]> {
  const { data } = await getSupabaseBrowserClient().auth.getSession(); if (!data.session?.access_token) throw new Error("session");
  const response = await fetch("/api/admin/deletion-requests", { headers: { authorization: `Bearer ${data.session.access_token}` } });
  if (!response.ok) throw new Error("unavailable"); return response.json() as Promise<Request[]>;
}
