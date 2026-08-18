import type { Metadata } from "next";
import Link from "next/link";
import styles from "./share.module.css";

export const metadata: Metadata = {
  title: "Shared medical file",
  robots: { index: false, follow: false },
  referrer: "no-referrer",
};

type SharePageProps = { params: Promise<{ token: string }> };

export default async function PublicSharePage({ params }: SharePageProps) {
  const { token } = await params;
  const apiUrl = process.env.API_URL;
  if (!apiUrl) return <Unavailable />;

  const response = await fetch(new URL(`/api/public/share/${encodeURIComponent(token)}`, apiUrl), {
    cache: "no-store",
    headers: { "referrer-policy": "no-referrer" },
  });
  if (!response.ok) return <Unavailable />;

  const share = await response.json() as { resourceType: "image" | "report" };
  const contentUrl = new URL(`/api/public/share/${encodeURIComponent(token)}/content`, apiUrl).toString();
  const viewerUrl = `${contentUrl}?disposition=inline`;
  return (
    <main className={styles.shell}>
      <section className={styles.card} aria-labelledby="share-title">
        <p className={styles.eyebrow}>Secure shared file</p>
        <h1 id="share-title">Your shared medical file</h1>
        <p>This link is private. Download the file only on a device you trust.</p>
        {share.resourceType === "image" ? <img /* eslint-disable-line @next/next/no-img-element -- public bytes stay on the no-store API path */ className={styles.image} src={viewerUrl} alt="Shared medical image" /> : <iframe className={styles.document} src={viewerUrl} title="Shared medical report" />}
        <div className={styles.actions}>
          <a className={styles.download} href={contentUrl} download>Download file</a>
          <Link className={styles.home} href="/">Patient Data Portal home</Link>
        </div>
      </section>
    </main>
  );
}

function Unavailable() {
  return (
    <main className={styles.shell}>
      <section className={styles.card} aria-labelledby="unavailable-title">
        <p className={styles.eyebrow}>Shared file</p>
        <h1 id="unavailable-title">This shared file is no longer available</h1>
        <p>The link may have expired or been revoked. Please contact the person who shared it with you.</p>
        <Link className={styles.home} href="/">Patient Data Portal home</Link>
      </section>
    </main>
  );
}
