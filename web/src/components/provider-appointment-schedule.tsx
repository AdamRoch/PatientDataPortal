"use client";

import { useCallback, useEffect, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./provider-appointment-schedule.module.css";

type Appointment = { id: string; startsAt: string; serviceName: string; status: string };
type Schedule = { timeZoneId: string; upcoming: Appointment[]; past: Appointment[] };

export function ProviderAppointmentSchedule() {
  const [schedule, setSchedule] = useState<Schedule | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [updating, setUpdating] = useState<string | null>(null);
  const refresh = useCallback(async () => {
    try { setSchedule(await request<Schedule>("/api/provider/appointments")); } catch { setMessage("We could not load appointments. Please try again."); }
  }, []);
  useEffect(() => { void request<Schedule>("/api/provider/appointments").then(setSchedule).catch(() => setMessage("We could not load appointments. Please try again.")); }, []);
  async function updateStatus(id: string, status: string) {
    setUpdating(`${id}-${status}`); setMessage(null);
    try { await request(`/api/provider/appointments/${id}/status`, "PATCH", { status }); await refresh(); setMessage(`Appointment marked ${status}.`); }
    catch { setMessage("We could not update that appointment. Its status may have changed."); }
    finally { setUpdating(null); }
  }

  if (!schedule) return <section aria-labelledby="provider-appointments-title"><h1 id="provider-appointments-title">Appointments</h1><p aria-busy="true">Loading appointments…</p></section>;
  return <section className={styles.schedule} aria-labelledby="provider-appointments-title">
    <h1 id="provider-appointments-title">Appointments</h1><p>Times are shown in your time zone: {schedule.timeZoneId}.</p>
    {message ? <p role="status">{message}</p> : null}
    <AppointmentList appointments={schedule.upcoming} empty="No upcoming appointments." label="Upcoming appointments" schedule={schedule} updating={updating} onUpdate={updateStatus} />
    <AppointmentList appointments={schedule.past} empty="No past appointments." label="Past appointments" schedule={schedule} updating={updating} onUpdate={updateStatus} />
  </section>;
}

function AppointmentList({ appointments, empty, label, schedule, updating, onUpdate }: { appointments: Appointment[]; empty: string; label: string; schedule: Schedule; updating: string | null; onUpdate: (id: string, status: string) => Promise<void> }) {
  return <section className={styles.group} aria-label={label}><h2>{label}</h2>{appointments.length === 0 ? <p>{empty}</p> : <ul className={styles.list}>{appointments.map(appointment => {
    const started = new Date(appointment.startsAt) <= new Date();
    const action = (status: string, label: string) => <button disabled={updating !== null} onClick={() => void onUpdate(appointment.id, status)} type="button">{updating === `${appointment.id}-${status}` ? "Saving…" : label}</button>;
    return <li className={styles.card} key={appointment.id}><div><strong>{appointment.serviceName}</strong><time dateTime={appointment.startsAt}>{formatTime(appointment.startsAt, schedule.timeZoneId)}</time><span>{appointment.status}</span></div>{appointment.status === "confirmed" ? <div className={styles.actions}>{action("completed", "Complete")}{started ? action("no-show", "No-show") : null}{action("cancelled", "Cancel")}</div> : null}</li>;
  })}</ul>}</section>;
}

function formatTime(value: string, timeZone: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short", timeZone }).format(new Date(value));
}

async function request<T>(url: string, method = "GET", body?: unknown): Promise<T> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  if (!data.session?.access_token) throw new Error("Your session has ended.");
  const response = await fetch(url, { method, headers: { authorization: `Bearer ${data.session.access_token}`, ...(body ? { "content-type": "application/json" } : {}) }, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) throw new Error("Appointment request failed.");
  return response.json() as Promise<T>;
}
