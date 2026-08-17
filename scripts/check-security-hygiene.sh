#!/usr/bin/env bash
set -euo pipefail

# Scan tracked and non-ignored working-tree files. This deliberately excludes ignored local
# environment files, which may contain real credentials and must not be read by this check.
if ! command -v gitleaks >/dev/null 2>&1; then
  echo 'gitleaks is required for the security hygiene sweep.' >&2
  exit 2
fi

scan_root="$(mktemp -d .security-scan.XXXXXX)"
cleanup() {
  rm -r "$scan_root"
}
trap cleanup EXIT

while IFS= read -r -d '' relative_path; do
  mkdir -p "$scan_root/$(dirname "$relative_path")"
  if [[ -L "$relative_path" ]]; then
    # Scan the link value without following it outside this worktree.
    readlink "$relative_path" > "$scan_root/$relative_path"
    continue
  fi

  cp -p "$relative_path" "$scan_root/$relative_path"
done < <(git ls-files -z --cached --others --exclude-standard)

if ! gitleaks dir --no-banner --no-color --redact=100 --log-level error "$scan_root" >/dev/null 2>&1; then
  echo 'Secret scan failed. Review the tracked working tree locally; findings are intentionally not printed.' >&2
  exit 1
fi

# These are deterministic synthetic seed values, not real patient data. Check committed runtime
# log/evidence formats for them so a retained log cannot silently introduce a PHI regression.
phi_pattern='Synthetic Patient [0-9]{3}|SYN-[0-9]{4}|(19[6-9][0-9]|200[0-5])-[0-9]{2}-[0-9]{2}|demo-(admin|provider|patient|unlinked)@patient-data-portal\.test'
while IFS= read -r evidence_file; do
  [[ -z "$evidence_file" ]] && continue
  if rg -q --pcre2 "$phi_pattern" "$evidence_file"; then
    echo 'Seeded PHI fixture found in committed runtime log or evidence output.' >&2
    exit 1
  fi
done < <(git ls-files --cached --others --exclude-standard | grep -E '\.(log|ndjson|jsonl|trace|har)$' || true)

# Keep the committed template aligned with every application or seed command setting.
required_env_keys=(
  SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY
  DATABASE_URL MIGRATION_DATABASE_URL APP_DB_ROLE APP_DB_PASSWORD
  DEMO_SEED_PASSWORD EMAIL_DELIVERY_MODE RESEND_API_KEY EMAIL_FROM APP_URL
  REMINDER_LEAD_MINUTES OUTBOX_JOB_SECRET OUTBOX_BATCH_SIZE OUTBOX_MAX_ATTEMPTS OUTBOX_LEASE_MINUTES
  AUDIT_HMAC_KEY IDENTITY_HMAC_KEY NEXT_PUBLIC_SUPABASE_URL NEXT_PUBLIC_SUPABASE_ANON_KEY API_URL
)
for key in "${required_env_keys[@]}"; do
  if ! grep -qE "^${key}=" .env.example; then
    echo ".env.example is missing $key." >&2
    exit 1
  fi
done

echo 'Security hygiene sweep passed: tracked files contain no detected secrets, no seeded PHI in runtime evidence, and a complete environment template.'
