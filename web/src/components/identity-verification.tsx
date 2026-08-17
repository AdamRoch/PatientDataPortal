"use client";

import { useEffect, useState, type FormEvent } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./identity-verification.module.css";

type VerificationState = "checking" | "unlinked" | "verified";
type Message = { kind: "error" | "success"; text: string } | null;

export function IdentityVerification() {
  const [state, setState] = useState<VerificationState>("checking");
  const [message, setMessage] = useState<Message>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    void requestIdentityStatus().then(({ verified }) => setState(verified ? "verified" : "unlinked")).catch(() => {
      setMessage({ kind: "error", text: "We could not check your verification status. Please try again later." });
      setState("unlinked");
    });
  }, []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setPending(true);
    setMessage(null);
    try {
      const result = await verifyIdentity({
        patientRef: String(form.get("patientRef") ?? ""),
        dob: String(form.get("dob") ?? ""),
      });
      if (!result.verified) throw new Error("identity_verification_failed");
      setState("verified");
      setMessage({ kind: "success", text: "Your identity has been verified. Your imaging and reports are now available." });
    } catch {
      setMessage({ kind: "error", text: "We could not verify your identity. Please try again later." });
    } finally {
      setPending(false);
    }
  }

  if (state === "checking") return <p aria-busy="true">Checking your patient access…</p>;

  return <section className={styles.section} aria-labelledby="verification-title">
    {state === "unlinked" ? <>
      <div className={styles.banner} role="status">
        <h2 id="verification-title">Verify your identity to access your care information</h2>
        <p>Enter the patient ID and date of birth provided by your care team.</p>
      </div>
      <form className={styles.form} onSubmit={submit}>
        <label htmlFor="patientRef">Patient ID</label>
        <input autoComplete="off" id="patientRef" name="patientRef" required />
        <label htmlFor="dob">Date of birth</label>
        <input id="dob" name="dob" required type="date" />
        {message && <p role={message.kind === "error" ? "alert" : "status"}>{message.text}</p>}
        <button disabled={pending} type="submit">{pending ? "Verifying…" : "Verify identity"}</button>
      </form>
    </> : <>
      <h2 id="verification-title">Care information</h2>
      {message && <p role="status">{message.text}</p>}
      <nav aria-label="Care information" className={styles.navigation}>
        <a href="/portal/imaging">Imaging</a>
        <a href="/portal/reports">Reports</a>
      </nav>
    </>}
  </section>;
}

async function requestIdentityStatus(): Promise<{ verified: boolean }> {
  return requestIdentity("GET");
}

async function verifyIdentity(body: { patientRef: string; dob: string }): Promise<{ verified: boolean }> {
  return requestIdentity("POST", body);
}

async function requestIdentity(method: "GET" | "POST", body?: { patientRef: string; dob: string }): Promise<{ verified: boolean }> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  const token = data.session?.access_token;
  if (!token) throw new Error("session_ended");
  const response = await fetch("/api/patient/identity", {
    method,
    headers: { authorization: `Bearer ${token}`, ...(body ? { "content-type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) throw new Error("identity_verification_failed");
  return response.json() as Promise<{ verified: boolean }>;
}
