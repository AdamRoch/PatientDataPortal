import { createClient } from "@supabase/supabase-js";
import { NextResponse } from "next/server";

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const authorization = request.headers.get("authorization");
  if (!authorization?.startsWith("Bearer ")) return NextResponse.json({ error: "Unauthorized" }, { status: 401 });

  const url = process.env.NEXT_PUBLIC_SUPABASE_URL;
  const key = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;
  const apiUrl = process.env.API_URL;
  if (!url || !key || !apiUrl) return NextResponse.json({ error: "Cine clips are unavailable" }, { status: 503 });

  const { data, error } = await createClient(url, key).auth.getUser(authorization.slice(7));
  if (error || !data.user) return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  if (!data.user.email_confirmed_at) return NextResponse.json({ error: "Email confirmation required" }, { status: 403 });

  const body = await request.json();
  const { id } = await params;
  const response = await fetch(new URL(`/api/cine/${encodeURIComponent(id)}/frame-urls`, apiUrl), {
    method: "POST",
    headers: { authorization, "content-type": "application/json" },
    body: JSON.stringify(body),
    cache: "no-store",
  });
  return new NextResponse(response.body, { status: response.status, headers: { "content-type": "application/json", "cache-control": "private, no-store" } });
}
