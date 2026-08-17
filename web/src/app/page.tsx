import { AuthCard } from "@/components/auth-card";
import styles from "./page.module.css";

type HomePageProps = {
  searchParams: Promise<{ reason?: string }>;
};

export default async function Home({ searchParams }: HomePageProps) {
  const { reason } = await searchParams;

  return (
    <main className={styles.page}>
      <section className={styles.intro} aria-labelledby="portal-title">
        <p className={styles.eyebrow}>Patient Data Portal</p>
        <h1 id="portal-title">Your care information, in one secure place.</h1>
        <p>
          Sign in to access your patient portal. New here? Create an account and
          confirm your email before continuing.
        </p>
        {reason === "session-expired" && (
          <p className={styles.notice} role="status">
            Your session has ended. Please sign in again.
          </p>
        )}
      </section>
      <AuthCard />
    </main>
  );
}
