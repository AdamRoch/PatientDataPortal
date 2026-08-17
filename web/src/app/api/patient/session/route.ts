import { createClient } from "@supabase/supabase-js";
import { NextResponse } from "next/server";

export async function GET(request: Request) {
  const authorization = request.headers.get("authorization");
  if (!authorization?.startsWith("Bearer ")) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  const url = process.env.NEXT_PUBLIC_SUPABASE_URL;
  const key = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;
  if (!url || !key) return NextResponse.json({ error: "Authentication is unavailable" }, { status: 503 });

  const { data, error } = await createClient(url, key).auth.getUser(authorization.slice(7));
  if (error || !data.user) return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  if (!data.user.email_confirmed_at) return NextResponse.json({ error: "Email confirmation required" }, { status: 403 });
  return NextResponse.json({ authenticated: true });
}
