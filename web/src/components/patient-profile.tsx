"use client";

import { useEffect, useState, type FormEvent } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";

type Profile = { displayName: string; timeZone: string };
type Message = { kind: "error" | "success"; text: string } | null;

export function PatientProfile() {
  const [profile, setProfile] = useState<Profile | null>(null);
  const [message, setMessage] = useState<Message>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    void requestProfile().then(setProfile).catch(() => setMessage({ kind: "error", text: "We could not load your profile." }));
  }, []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setPending(true);
    setMessage(null);
    try {
      const updated = await requestProfile({
        displayName: String(form.get("displayName") ?? ""),
        timeZone: String(form.get("timeZone") ?? ""),
      });
      setProfile(updated);
      setMessage({ kind: "success", text: "Your profile has been updated." });
    } catch (error) {
      setMessage({ kind: "error", text: error instanceof Error ? error.message : "We could not update your profile." });
    } finally {
      setPending(false);
    }
  }

  if (!profile) return <p aria-busy="true">Loading your profile…</p>;

  return <section aria-labelledby="profile-title">
    <h2 id="profile-title">Profile</h2>
    <form onSubmit={submit}>
      <label htmlFor="displayName">Display name</label>
      <input defaultValue={profile.displayName} id="displayName" maxLength={120} name="displayName" required />
      <label htmlFor="timeZone">Time zone</label>
      <input defaultValue={profile.timeZone} id="timeZone" maxLength={64} name="timeZone" required />
      {message && <p role={message.kind === "error" ? "alert" : "status"}>{message.text}</p>}
      <button disabled={pending} type="submit">{pending ? "Saving…" : "Save profile"}</button>
    </form>
  </section>;
}

async function requestProfile(update?: Profile): Promise<Profile> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  const token = data.session?.access_token;
  if (!token) throw new Error("Your session has ended. Please sign in again.");
  const response = await fetch("/api/patient/profile", {
    method: update ? "PUT" : "GET",
    headers: { authorization: `Bearer ${token}`, ...(update ? { "content-type": "application/json" } : {}) },
    body: update ? JSON.stringify(update) : undefined,
  });
  if (!response.ok) throw new Error(response.status === 400 ? "Enter a valid display name and time zone." : "We could not update your profile.");
  return response.json() as Promise<Profile>;
}
