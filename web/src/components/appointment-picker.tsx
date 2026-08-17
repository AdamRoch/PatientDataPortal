"use client";

import { useEffect, useMemo, useState } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./appointment-picker.module.css";

type Service = { id: string; name: string; active: boolean };
type Provider = { id: string; name: string; services: Service[] };
type Slot = { id: string; startsAt: string; endsAt: string };

export function AppointmentPicker() {
  const [providers, setProviders] = useState<Provider[] | null>(null);
  const [providerId, setProviderId] = useState("");
  const [serviceId, setServiceId] = useState("");
  const [slots, setSlots] = useState<Slot[] | null>(null);
  const [selectedSlot, setSelectedSlot] = useState("");
  const [error, setError] = useState(false);
  const zone = useMemo(() => Intl.DateTimeFormat().resolvedOptions().timeZone || "your local time zone", []);
  const provider = providers?.find((item) => item.id === providerId);

  useEffect(() => { void request<Provider[]>("/api/patient/providers").then(setProviders).catch(() => setError(true)); }, []);
  useEffect(() => {
    if (!providerId || !serviceId) return;
    let active = true;
    const from = new Date();
    const to = new Date(from); to.setDate(to.getDate() + 14);
    void request<Slot[]>(`/api/patient/providers/${encodeURIComponent(providerId)}/slots?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`).then((result) => { if (active) setSlots(result); }).catch(() => { if (active) setError(true); });
    return () => { active = false; };
  }, [providerId, serviceId]);

  const days = useMemo(() => {
    const formatter = new Intl.DateTimeFormat(undefined, { weekday: "long", month: "long", day: "numeric" });
    return (slots ?? []).reduce<Map<string, Slot[]>>((grouped, slot) => {
      const day = formatter.format(new Date(slot.startsAt));
      grouped.set(day, [...(grouped.get(day) ?? []), slot]);
      return grouped;
    }, new Map());
  }, [slots]);

  return <section id="appointment-picker" className={styles.picker} aria-labelledby="appointment-picker-heading">
    <h2 id="appointment-picker-heading">Book an appointment</h2>
    <p className={styles.zone}>Times shown in {zone}.</p>
    {error ? <p role="alert">We could not load appointment availability. Please try again later.</p> : null}
    {!providers ? <p aria-busy="true">Loading providers…</p> : <>
      <div className={styles.fields}>
        <label className={styles.field} htmlFor="provider">Provider
          <select id="provider" value={providerId} onChange={(event) => { setProviderId(event.target.value); setServiceId(""); setSlots(null); setSelectedSlot(""); }}><option value="">Choose a provider</option>{providers.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select>
        </label>
        <label className={styles.field} htmlFor="service">Service
          <select id="service" value={serviceId} disabled={!provider} onChange={(event) => { setServiceId(event.target.value); setSlots(null); setSelectedSlot(""); }}><option value="">Choose a service</option>{provider?.services.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select>
        </label>
      </div>
      {provider && !serviceId ? <p>Choose a service to see available times.</p> : null}
      {serviceId && slots === null ? <p aria-busy="true">Loading available times…</p> : null}
      {serviceId && slots?.length === 0 ? <p>No open appointment times are available in the next two weeks.</p> : null}
      {serviceId && [...days.entries()].map(([day, daySlots]) => <section className={styles.day} key={day}><h3>{day}</h3><ul className={styles.times}>{daySlots.map((slot) => <li key={slot.id}><button className={selectedSlot === slot.id ? styles.selected : undefined} type="button" aria-pressed={selectedSlot === slot.id} onClick={() => setSelectedSlot(slot.id)}>{new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(new Date(slot.startsAt))}</button></li>)}</ul></section>)}
    </>}
  </section>;
}

async function request<T>(path: string): Promise<T> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  const token = data.session?.access_token;
  if (!token) throw new Error("session_ended");
  const response = await fetch(path, { headers: { authorization: `Bearer ${token}` } });
  if (!response.ok) throw new Error("availability_unavailable");
  return response.json() as Promise<T>;
}
