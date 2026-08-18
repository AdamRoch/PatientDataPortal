import Link from "next/link";
import styles from "./portal-navigation.module.css";

export function PortalNavigation() {
  return <nav className={styles.navigation} aria-label="Patient portal"><Link href="/portal">← Back to portal</Link></nav>;
}
