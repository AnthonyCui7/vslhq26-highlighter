#!/usr/bin/env bash
# Edge-case probe of the Highlighter API: auth, input validation, per-user
# scoping, and route hardening. Complements e2e-smoke.sh (happy path) with the
# unhappy paths a live demo can hit.
#
#   usage: BASE=http://localhost:5199 scripts/edge-smoke.sh
#
# Safe by construction: every project create in here is invalid on purpose, so
# no rows are inserted and no worker is spawned. Signup does create one
# ephemeral auth user per run (same as e2e-smoke.sh).
set -uo pipefail

BASE="${BASE:-http://localhost:5199}"
PASS=0; FAIL=0

check() { # check <name> <expected-status> <actual-status>
  if [[ "$3" == "$2" ]]; then
    PASS=$((PASS + 1)); printf '\033[32mPASS\033[0m %-52s %s\n' "$1" "$3"
  else
    FAIL=$((FAIL + 1)); printf '\033[31mFAIL\033[0m %-52s got %s, wanted %s\n' "$1" "$3" "$2"
  fi
}

status() { # status <method> <path> [json-body] [token]
  local method=$1 path=$2 body=${3:-} token=${4:-}
  local args=(-sS -o /tmp/edge-body.json -w '%{http_code}' -X "$method" "$BASE$path")
  [[ -n "$body" ]] && args+=(-H 'content-type: application/json' -d "$body")
  [[ -n "$token" ]] && args+=(-H "Authorization: Bearer $token")
  curl "${args[@]}" 2>/dev/null
}

field() {
  python3 -c '
import json, sys
value = json.load(sys.stdin)
for key in sys.argv[1].split("."):
    value = value[int(key)] if isinstance(value, list) else value[key]
print(value)' "$1" 2>/dev/null
}

echo "== auth gate =="
check "tokenless list is rejected"        401 "$(status GET /api/projects)"
check "garbage token is rejected"         401 "$(status GET /api/projects '' 'not-a-jwt')"
check "expired-shape token is rejected"   401 "$(status GET /api/projects '' 'eyJhbGciOiJFUzI1NiJ9.e30.sig')"

# NOTE: bodies live in variables — macOS bash 3.2 mis-parses escaped quotes
# nested inside "$(...)" (brace expansion mangles the JSON).
EMAIL="edge+$(date +%s)@example.com"
CREDS='{"email":"'"$EMAIL"'","password":"edge-pass-123"}'
BAD_CREDS='{"email":"'"$EMAIL"'","password":"wrong"}'
SESSION_STATUS=$(status POST /api/auth/signup "$CREDS")
check "signup works" 200 "$SESSION_STATUS"
TOKEN=$(field accessToken < /tmp/edge-body.json)
if [[ -z "$TOKEN" ]]; then echo "no token — aborting"; exit 1; fi

check "duplicate signup is rejected" 409 "$(status POST /api/auth/signup "$CREDS")"
check "wrong password is rejected"   401 "$(status POST /api/auth/login "$BAD_CREDS")"
check "empty-body login is 400"      400 "$(status POST /api/auth/login '{}')"

echo "== project create validation (none of these may insert a row) =="
check "missing sourceUrl"      400 "$(status POST /api/projects '{"pipeline":"both"}' "$TOKEN")"
check "unsupported host"       400 "$(status POST /api/projects '{"sourceUrl":"https://vimeo.com/123","pipeline":"both"}' "$TOKEN")"
check "not a url at all"       400 "$(status POST /api/projects '{"sourceUrl":"not a url","pipeline":"both"}' "$TOKEN")"
check "bad pipeline"           400 "$(status POST /api/projects '{"sourceUrl":"https://youtu.be/jNQXAC9IVRw","pipeline":"vertical"}' "$TOKEN")"
check "minClipScore > 1"       400 "$(status POST /api/projects '{"sourceUrl":"https://youtu.be/jNQXAC9IVRw","pipeline":"both","minClipScore":2}' "$TOKEN")"
check "negative minClipScore"  400 "$(status POST /api/projects '{"sourceUrl":"https://youtu.be/jNQXAC9IVRw","pipeline":"both","minClipScore":-0.5}' "$TOKEN")"
check "garbage targetMinutes"  400 "$(status POST /api/projects '{"sourceUrl":"https://youtu.be/jNQXAC9IVRw","pipeline":"long","targetMinutes":"abc"}' "$TOKEN")"
check "tiny chunkSeconds"      400 "$(status POST /api/projects '{"sourceUrl":"https://youtu.be/jNQXAC9IVRw","pipeline":"both","chunkSeconds":5}' "$TOKEN")"
check "huge chunkSeconds"      400 "$(status POST /api/projects '{"sourceUrl":"https://youtu.be/jNQXAC9IVRw","pipeline":"both","chunkSeconds":9999}' "$TOKEN")"
check "negative maxChunks"     400 "$(status POST /api/projects '{"sourceUrl":"https://youtu.be/jNQXAC9IVRw","pipeline":"both","maxChunks":-1}' "$TOKEN")"
check "malformed json body"    400 "$(status POST /api/projects '{"sourceUrl":' "$TOKEN")"

echo "== scoping and lookups =="
GHOST="00000000-0000-0000-0000-000000000001"
check "unknown project reads as 404"    404 "$(status GET "/api/projects/$GHOST" '' "$TOKEN")"
check "unknown project delete is 404"   404 "$(status DELETE "/api/projects/$GHOST" '' "$TOKEN")"
check "unknown project clips is 404"    404 "$(status GET "/api/projects/$GHOST/clips" '' "$TOKEN")"
check "non-guid project id is 404"      404 "$(status GET "/api/projects/not-a-guid" '' "$TOKEN")"
check "bad clip filter is 400"          400 "$(status GET "/api/projects/$GHOST/clips?pipeline=nope" '' "$TOKEN")"
check "bad clip order is 400"           400 "$(status GET "/api/projects/$GHOST/clips?order=nope" '' "$TOKEN")"
check "unknown job is 404"              404 "$(status GET "/api/jobs/nope" '' "$TOKEN")"
check "revise on unknown project 404"   404 "$(status POST "/api/projects/$GHOST/revise" '{"instructions":"x"}' "$TOKEN")"
check "publish on unknown project 404"  404 "$(status POST "/api/projects/$GHOST/publish" '{"target":"longform","platforms":["youtube"],"dryRun":true}' "$TOKEN")"
check "cancel on unknown project 404"   404 "$(status POST "/api/projects/$GHOST/cancel" '' "$TOKEN")"

echo
echo "passed $PASS, failed $FAIL"
exit $((FAIL > 0))
