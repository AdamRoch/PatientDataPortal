import { ReportsViewer } from "@/components/reports-viewer";
import { PortalNavigation } from "@/components/portal-navigation";

export default function ReportsPage() {
  return <main>
    <PortalNavigation />
    <h1>Reports</h1>
    <p>Signed reports from your care record.</p>
    <ReportsViewer />
  </main>;
}
