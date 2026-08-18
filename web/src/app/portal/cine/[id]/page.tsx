import { CinePlayer } from "@/components/cine-player";
import { PortalNavigation } from "@/components/portal-navigation";

export default async function CinePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <main><PortalNavigation /><h1>Cine player</h1><CinePlayer clipId={id} /></main>;
}
