import { StudiesList } from "@/components/studies-list";
import { PortalNavigation } from "@/components/portal-navigation";

export default function ImagingPage() {
  return <main>
    <PortalNavigation />
    <h1>Imaging</h1>
    <p>Completed studies from your care record.</p>
    <StudiesList />
  </main>;
}
