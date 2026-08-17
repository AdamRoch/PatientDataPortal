import { createClient, type SupabaseClient } from "@supabase/supabase-js";

let client: SupabaseClient | undefined;

export function getSupabaseBrowserClient(): SupabaseClient {
  const url = process.env.NEXT_PUBLIC_SUPABASE_URL;
  const key = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;

  if (!url || !key) {
    throw new Error("Authentication is not configured for this environment.");
  }

  client ??= createClient(url, key);
  return client;
}

export function hasVerifiedEmail(emailConfirmedAt: string | null | undefined) {
  return Boolean(emailConfirmedAt);
}
