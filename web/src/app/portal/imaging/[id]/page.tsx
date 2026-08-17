import { ImageViewer } from "@/components/image-viewer";

export default async function ImagePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <main><h1>Image viewer</h1><ImageViewer imageId={id} /></main>;
}
