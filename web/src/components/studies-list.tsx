"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { getSupabaseBrowserClient } from "@/lib/auth/client";

type Study = { id: string; performedAt: string; description: string; imageIds?: string[] };

export function StudiesList() {
  const [studies, setStudies] = useState<Study[] | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    void requestStudies().then(setStudies).catch(() => setError(true));
  }, []);

  if (error) return <p role="alert">We could not load your studies. Please try again later.</p>;
  if (!studies) return <p aria-busy="true">Loading your studies…</p>;
  if (studies.length === 0) return <p>No completed studies are available yet.</p>;

  return <ul aria-label="Completed studies">
    {studies.map((study) => <li key={study.id}>
      <strong>{study.description}</strong><br />
      <time dateTime={study.performedAt}>{new Intl.DateTimeFormat(undefined, { dateStyle: "long" }).format(new Date(study.performedAt))}</time>
      {study.imageIds?.length ? <ul aria-label={`Images from ${study.description}`}>
        {study.imageIds.map((imageId, index) => <li key={imageId}><Link href={`/portal/imaging/${imageId}`}>View image {index + 1}</Link></li>)}
      </ul> : null}
    </li>)}
  </ul>;
}

async function requestStudies(): Promise<Study[]> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  const token = data.session?.access_token;
  if (!token) throw new Error("session_ended");
  const response = await fetch("/api/patient/studies", { headers: { authorization: `Bearer ${token}` } });
  if (!response.ok) throw new Error("studies_unavailable");
  return response.json() as Promise<Study[]>;
}
