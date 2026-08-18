import { ImageViewer } from "@/components/image-viewer";
import { PortalNavigation } from "@/components/portal-navigation";

export default async function ImagePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <main><PortalNavigation /><h1>Image viewer</h1><ImageViewer imageId={id} /></main>;
}
