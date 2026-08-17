#!/usr/bin/env bash
set -euo pipefail

source_file="api/Identity/IdentityVerificationService.cs"

# This is a code-review gate, not a timing-distribution test: CI timing samples are noisy and
# would turn a security invariant into a flaky assertion. The service must retain its dummy
# record and fixed-size digest comparisons on the unknown-reference path.
rg -q '__dummy_patient_reference__' "$source_file"
rg -q 'CryptographicOperations.FixedTimeEquals' "$source_file"
rg -q 'SHA256.HashData' "$source_file"
echo 'Identity verification retains the unknown-reference dummy compare path.'
