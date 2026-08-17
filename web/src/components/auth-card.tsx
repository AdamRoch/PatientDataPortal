"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { getSupabaseBrowserClient } from "@/lib/auth/client";
import styles from "./auth-card.module.css";

type Mode = "sign-in" | "register";
type Message = { kind: "error" | "success"; text: string } | null;

export function AuthCard() {
  const router = useRouter();
  const [mode, setMode] = useState<Mode>("sign-in");
  const [message, setMessage] = useState<Message>(null);
  const [pending, setPending] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const email = String(form.get("email") ?? "").trim();
    const password = String(form.get("password") ?? "");
    setMessage(null);
    setPending(true);

    try {
      const supabase = getSupabaseBrowserClient();
      if (mode === "register") {
        const { error } = await supabase.auth.signUp({
          email,
          password,
          options: { emailRedirectTo: `${window.location.origin}/` },
        });
        if (error) throw error;
        setMessage({
          kind: "success",
          text: "Check your inbox to confirm your email. You can sign in after confirmation.",
        });
        return;
      }

      const { data, error } = await supabase.auth.signInWithPassword({ email, password });
      if (error) throw error;
      if (!data.user.email_confirmed_at) {
        await supabase.auth.signOut();
        setMessage({
          kind: "error",
          text: "Confirm your email before accessing the patient portal. Check your inbox for the confirmation link.",
        });
        return;
      }
      router.replace("/portal");
    } catch (error) {
      setMessage({
        kind: "error",
        text: error instanceof Error ? error.message : "We could not complete that request. Please try again.",
      });
    } finally {
      setPending(false);
    }
  }

  function changeMode(nextMode: Mode) {
    setMode(nextMode);
    setMessage(null);
  }

  return (
    <section className={styles.card} aria-labelledby="auth-title">
      <div className={styles.tabs} aria-label="Account action">
        <button className={mode === "sign-in" ? styles.activeTab : styles.tab} onClick={() => changeMode("sign-in")} type="button">Sign in</button>
        <button className={mode === "register" ? styles.activeTab : styles.tab} onClick={() => changeMode("register")} type="button">Create account</button>
      </div>
      <h2 id="auth-title">{mode === "sign-in" ? "Welcome back" : "Create your account"}</h2>
      <p className={styles.helper}>{mode === "sign-in" ? "Use the email and password you registered with." : "We will send a confirmation link before you can access the portal."}</p>
      <form className={styles.form} onSubmit={submit}>
        <label htmlFor="email">Email address</label>
        <input autoComplete="email" id="email" name="email" required type="email" />
        <label htmlFor="password">Password</label>
        <input autoComplete={mode === "sign-in" ? "current-password" : "new-password"} id="password" minLength={6} name="password" required type="password" />
        {message && <p className={message.kind === "error" ? styles.error : styles.success} role={message.kind === "error" ? "alert" : "status"}>{message.text}</p>}
        <button className={styles.submit} disabled={pending} type="submit">{pending ? "Please wait…" : mode === "sign-in" ? "Sign in" : "Create account"}</button>
      </form>
    </section>
  );
}
