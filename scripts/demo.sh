#!/usr/bin/env bash
# Walks through every path of the flow against running services (start them with ./scripts/run.sh).
# The fake providers choose outcomes from the last name: "Match", "Forged", "Flaky". Needs curl and jq.
set -euo pipefail
API=http://localhost:5100
OFFICE=http://localhost:5200
JPEG='/9j/4AAQSkZJRg=='
PNG='iVBORw0KGgoAAA=='

create() { # lastName [country] [nationalId]
  curl -s -X POST "$API/applications" -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen 2>/dev/null || date +%s%N)" -d @- <<EOF
{"firstName":"Ana","lastName":"$1","dateOfBirth":"1991-03-04","country":"${2:-MB}","nationalId":"${3:-0403991450016}",
 "email":"ana@example.com","phone":"+38970000000","termsAccepted":true,
 "documents":[{"type":"PASSPORT","image":"$JPEG"},{"type":"SELFIE","image":"$PNG"}]}
EOF
}
status() { curl -s "$API/applications/$(jq -r .applicationId <<<"$1")?waitSeconds=${2:-0}" -H "X-Application-Token: $(jq -r .accessToken <<<"$1")"; }
show() { printf '\n\033[36m== %s\033[0m\n' "$1"; jq . <<<"$2"; }

clean=$(create Petrovska);                       show "1. Clean applicant, MB: one call in, one answer out" "$clean"
match=$(create Match);                      show "2. Possible sanctions match, MB: referred, not rejected" "$match"
mid=$(jq -r .applicationId <<<"$match")
show "   MB officer queue (no personal data in the list)" "$(curl -s "$OFFICE/review/queue" -H 'X-Staff-Id: officer.mb')"
printf '   MA officer opening an MB application -> HTTP %s\n' "$(curl -s -o /dev/null -w '%{http_code}' "$OFFICE/review/applications/$mid" -H 'X-Staff-Id: officer.ma')"
curl -s -o /dev/null -X POST "$OFFICE/review/applications/$mid/decision" -H 'X-Staff-Id: officer.mb' -H 'Content-Type: application/json' \
  -d '{"decision":"APPROVE","notes":"False positive: date of birth does not match the listed person"}'
show "   After officer.mb approves, the customer sees" "$(status "$match")"

md=$(create Petrovska MD 0403991450);            show "3. Clean applicant, MD: approved but must sign in branch" "$md"
curl -s -o /dev/null -X POST "$OFFICE/branch/applications/$(jq -r .applicationId <<<"$md")/activate" -H 'X-Staff-Id: branch.md' \
  -H 'Content-Type: application/json' -d '{"wetSignatureCaptured":true}'
show "   After branch.md captures the wet signature" "$(status "$md")"

flaky=$(create Flaky);                         show "4. Each provider returns 503 once: retried with backoff, still answered in the same call" "$flaky"

show "5. Document fails verification" "$(create Forged)"
show "6. Access log for the referred application (as officer.mb)" "$(curl -s "$OFFICE/review/applications/$mid/audit" -H 'X-Staff-Id: officer.mb')"
