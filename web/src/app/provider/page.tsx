"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { getSupabaseBrowserClient, hasVerifiedEmail } from "@/lib/auth/client";
import { ProviderScheduleSettings } from "@/components/provider-schedule-settings";
import { ProviderAppointmentSchedule } from "@/components/provider-appointment-schedule";
import styles from "../portal/portal.module.css";

export default function ProviderPage() {
  const router = useRouter(); const [ready, setReady] = useState(false);
  useEffect(() => { let active = true; try { const client = getSupabaseBrowserClient(); void client.auth.getSession().then(({ data }) => { if (!data.session || !hasVerifiedEmail(data.session.user.email_confirmed_at)) router.replace("/?reason=session-expired"); else if (active) setReady(true); }); } catch { router.replace("/?reason=session-expired"); } return () => { active = false; }; }, [router]);
  return ready ? <main className={styles.page}><ProviderAppointmentSchedule /><ProviderScheduleSettings /></main> : <main aria-busy="true">Checking your secure session…</main>;
}
