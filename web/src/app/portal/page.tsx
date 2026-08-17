"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { getSupabaseBrowserClient, hasVerifiedEmail } from "@/lib/auth/client";
import { PatientProfile } from "@/components/patient-profile";

export default function PortalPage() {
  const router = useRouter();
  const [ready, setReady] = useState(false);

  useEffect(() => {
    let active = true;
    const redirectToLogin = () => router.replace("/?reason=session-expired");
    let supabase;

    try {
      supabase = getSupabaseBrowserClient();
    } catch {
      redirectToLogin();
      return;
    }

    void supabase.auth.getSession().then(({ data, error }) => {
      if (error || !data.session || !hasVerifiedEmail(data.session.user.email_confirmed_at)) {
        redirectToLogin();
        return;
      }
      if (active) setReady(true);
    });

    const { data: listener } = supabase.auth.onAuthStateChange((event, session) => {
      if (event === "SIGNED_OUT" || !session || !hasVerifiedEmail(session.user.email_confirmed_at)) redirectToLogin();
    });
    return () => { active = false; listener.subscription.unsubscribe(); };
  }, [router]);

  if (!ready) return <main aria-busy="true">Checking your secure session…</main>;

  return <main><h1>Patient portal</h1><p>Your account is signed in. Patient records will appear here after identity verification.</p><PatientProfile /><SignOutButton /></main>;
}

function SignOutButton() {
  const router = useRouter();
  async function signOut() { await getSupabaseBrowserClient().auth.signOut(); router.replace("/"); }
  return <button onClick={signOut} type="button">Sign out</button>;
}
