"use client";

import { useEffect, useState, type FormEvent } from "react";
import { getSupabaseBrowserClient } from "@/lib/auth/client";

type Rule = { weekday: number; localStart: string; localEnd: string; effectiveFrom?: string; effectiveUntil?: string | null };
type Block = { id: string; startsAt: string; endsAt: string };
type Service = { id: string; name: string; active: boolean };
type Schedule = { slotLengthMinutes: number; workingHours: Rule[]; blockedTimes: Block[]; services: Service[] };
const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

export function ProviderScheduleSettings() {
  const [schedule, setSchedule] = useState<Schedule | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  useEffect(() => { void request<Schedule>("/api/provider/schedule").then(setSchedule).catch(() => setMessage("We could not load schedule settings.")); }, []);
  async function refresh() { setSchedule(await request<Schedule>("/api/provider/schedule")); }
  async function saveHours(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const form = new FormData(event.currentTarget);
    const rules = weekdays.flatMap((_, weekday) => form.get(`enabled-${weekday}`) ? [{ weekday, localStart: String(form.get(`start-${weekday}`)), localEnd: String(form.get(`end-${weekday}`)) }] : []);
    await request("/api/provider/schedule/working-hours", "PUT", { rules }); await refresh(); setMessage("Working hours saved.");
  }
  async function saveSlotLength(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const form = new FormData(event.currentTarget); await request("/api/provider/schedule/slot-length", "PUT", { slotLengthMinutes: Number(form.get("slotLengthMinutes")) }); await refresh(); setMessage("Slot length saved."); }
  async function addBlock(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const form = new FormData(event.currentTarget); await request("/api/provider/schedule/blocks", "POST", { startsAt: new Date(String(form.get("startsAt"))).toISOString(), endsAt: new Date(String(form.get("endsAt"))).toISOString() }); event.currentTarget.reset(); await refresh(); setMessage("Blocked time added."); }
  async function addService(event: FormEvent<HTMLFormElement>) { event.preventDefault(); const form = new FormData(event.currentTarget); await request("/api/provider/schedule/services", "POST", { name: String(form.get("name")), active: true }); event.currentTarget.reset(); await refresh(); setMessage("Service added."); }
  if (!schedule) return <p aria-busy="true">Loading schedule settings…</p>;
  const byDay = new Map(schedule.workingHours.map(rule => [rule.weekday, rule]));
  return <section aria-labelledby="schedule-title"><h1 id="schedule-title">Schedule settings</h1><p>Set the hours and services patients will be able to choose from.</p>
    {message && <p role="status">{message}</p>}
    <form onSubmit={saveHours}><h2>Working hours</h2>{weekdays.map((day, weekday) => { const rule = byDay.get(weekday); return <fieldset key={day}><legend>{day}</legend><label><input defaultChecked={Boolean(rule)} name={`enabled-${weekday}`} type="checkbox" /> Available</label><label>Start <input defaultValue={rule?.localStart ?? "09:00"} name={`start-${weekday}`} required type="time" /></label><label>End <input defaultValue={rule?.localEnd ?? "17:00"} name={`end-${weekday}`} required type="time" /></label></fieldset>; })}<button type="submit">Save working hours</button></form>
    <form onSubmit={saveSlotLength}><h2>Appointment length</h2><label>Minutes <input defaultValue={schedule.slotLengthMinutes} max="480" min="5" name="slotLengthMinutes" required step="5" type="number" /></label><button type="submit">Save length</button></form>
    <form onSubmit={addBlock}><h2>Blocked time</h2><label>Starts <input name="startsAt" required type="datetime-local" /></label><label>Ends <input name="endsAt" required type="datetime-local" /></label><button type="submit">Add blocked time</button></form>
    <ul>{schedule.blockedTimes.map(block => <li key={block.id}>{new Date(block.startsAt).toLocaleString()} to {new Date(block.endsAt).toLocaleString()} <button onClick={() => void request(`/api/provider/schedule/blocks/${block.id}`, "DELETE").then(refresh)} type="button">Remove</button></li>)}</ul>
    <form onSubmit={addService}><h2>Services</h2><label>Service name <input maxLength={120} name="name" required /></label><button type="submit">Add service</button></form>
    <ul>{schedule.services.map(service => <li key={service.id}><label><input checked={service.active} onChange={event => void request(`/api/provider/schedule/services/${service.id}`, "PUT", { name: service.name, active: event.target.checked }).then(refresh)} type="checkbox" /> {service.name}</label><button onClick={() => void request(`/api/provider/schedule/services/${service.id}`, "DELETE").then(refresh)} type="button">Remove</button></li>)}</ul>
  </section>;
}

async function request<T>(url: string, method = "GET", body?: unknown): Promise<T> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  if (!data.session?.access_token) throw new Error("Your session has ended. Please sign in again.");
  const response = await fetch(url, { method, headers: { authorization: `Bearer ${data.session.access_token}`, ...(body ? { "content-type": "application/json" } : {}) }, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) throw new Error("We could not save those schedule settings.");
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
