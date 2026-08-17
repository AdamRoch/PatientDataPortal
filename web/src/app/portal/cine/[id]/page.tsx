import { CinePlayer } from "@/components/cine-player";

export default async function CinePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <main><h1>Cine player</h1><CinePlayer clipId={id} /></main>;
}
