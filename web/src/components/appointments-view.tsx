"use client";

import { useEffect, useMemo, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./appointments-view.module.css";

type Appointment = { id: string; providerName: string; serviceName: string; startsAt: string; status: string };
type AppointmentGroups = { upcoming: Appointment[]; past: Appointment[] };

const statusLabels: Record<string, string> = { confirmed: "Confirmed", requested: "Requested", completed: "Completed", cancelled: "Cancelled", "no-show": "No show" };

export function AppointmentsView() {
  const [appointments, setAppointments] = useState<AppointmentGroups | null>(null);
  const [error, setError] = useState(false);
  const [cancelling, setCancelling] = useState<string | null>(null);
  const [now] = useState(() => Date.now());
  const format = useMemo(() => new Intl.DateTimeFormat(undefined, { weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }), []);

  useEffect(() => { void request<AppointmentGroups>("/api/patient/appointments").then(setAppointments).catch(() => setError(true)); }, []);

  async function cancel(appointment: Appointment) {
    if (!window.confirm(`Cancel your ${appointment.serviceName} appointment with ${appointment.providerName}?`)) return;
    setCancelling(appointment.id);
    try { await request<void>(`/api/patient/appointments/${encodeURIComponent(appointment.id)}`, "DELETE"); setError(false); setAppointments(await request<AppointmentGroups>("/api/patient/appointments")); }
    catch { setError(true); }
    finally { setCancelling(null); }
  }

  return <section className={styles.appointments} aria-labelledby="appointments-heading">
    <div className={styles.heading}><h2 id="appointments-heading">Your appointments</h2><a href="#appointment-picker">Book an appointment</a></div>
    {error ? <p role="alert">We could not update your appointments. Please try again.</p> : null}
    {!appointments ? <p aria-busy="true">Loading appointments…</p> : appointments.upcoming.length === 0 && appointments.past.length === 0 ? <div className={styles.empty}><h3>No appointments yet</h3><p>When you book an appointment, its details will appear here.</p><a href="#appointment-picker">Find an appointment time</a></div> : <>
      <AppointmentSection title="Upcoming" appointments={appointments.upcoming} format={format} now={now} cancelling={cancelling} onCancel={cancel} />
      <AppointmentSection title="Past" appointments={appointments.past} format={format} now={now} cancelling={cancelling} onCancel={cancel} />
    </>}
  </section>;
}

function AppointmentSection({ title, appointments, format, now, cancelling, onCancel }: { title: string; appointments: Appointment[]; format: Intl.DateTimeFormat; now: number; cancelling: string | null; onCancel: (appointment: Appointment) => Promise<void> }) {
  if (appointments.length === 0) return null;
  return <section className={styles.section} aria-labelledby={`${title.toLowerCase()}-appointments`}><h3 id={`${title.toLowerCase()}-appointments`}>{title}</h3><ul className={styles.list}>{appointments.map((appointment) => {
    const canChange = appointment.status === "confirmed" && new Date(appointment.startsAt).getTime() - now >= 24 * 60 * 60 * 1000;
    return <li className={styles.card} key={appointment.id}><div><p className={styles.date}>{format.format(new Date(appointment.startsAt))}</p><p>{appointment.serviceName} with {appointment.providerName}</p><span className={`${styles.status} ${styles[`status${appointment.status.replace("-", "")}`] ?? ""}`}>Status: {statusLabels[appointment.status] ?? appointment.status}</span></div>{canChange ? <div className={styles.actions}><a href="#appointment-picker">Reschedule</a><button type="button" disabled={cancelling === appointment.id} onClick={() => void onCancel(appointment)}>{cancelling === appointment.id ? "Cancelling…" : "Cancel appointment"}</button></div> : null}</li>;
  })}</ul></section>;
}

async function request<T>(path: string, method = "GET"): Promise<T> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  const token = data.session?.access_token;
  if (!token) throw new Error("session_ended");
  const response = await fetch(path, { method, headers: { authorization: `Bearer ${token}` } });
  if (!response.ok) throw new Error("appointments_unavailable");
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
