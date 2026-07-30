#!/usr/bin/env bash
# End-to-end smoke of the Highlighter API against a real (short!) YouTube VOD.
#
#   usage: BASE=http://localhost:5199 scripts/e2e-smoke.sh <youtube VOD url>
#
# Prereqs: the API is running, `dotnet build pipeline-dotnet` has produced the
# worker, and the repo-root .env carries live keys (python3 is used for JSON).
# The run ingests 2 chunks (~3 min of source) in `both` mode, revises the
# long-form cut, dry-run publishes, then exercises cancel/delete/cleanup on a
# second project.
set -euo pipefail

BASE="${BASE:-http://localhost:5199}"
VOD="${1:?usage: e2e-smoke.sh <short youtube VOD url>}"

say() { printf '\n\033[1;32m== %s\033[0m\n' "$*"; }
get() { curl -sS -H "Authorization: Bearer $TOKEN" "$BASE$1"; }
post() { curl -sS -X POST "$BASE$1" -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' ${2:+-d "$2"}; }
field() { # field <dot.path>  — reads JSON on stdin, prints the value
  python3 -c '
import json, sys
value = json.load(sys.stdin)
for key in sys.argv[1].split("."):
    value = value[int(key)] if isinstance(value, list) else value[key]
print(value)' "$1"
}

wait_status() { # wait_status <project-id> <target-status> <timeout-seconds>
  local id=$1 target=$2 timeout=$3 status elapsed=0
  while true; do
    status=$(get "/api/projects/$id" | field project.status)
    [[ "$status" == "$target" ]] && return 0
    [[ "$status" == "failed" ]] && {
      echo "project failed: $(get "/api/projects/$id" | field project.error)"; return 1; }
    (( elapsed >= timeout )) && { echo "timeout waiting for $target (still $status)"; return 1; }
    sleep 5; elapsed=$((elapsed + 5))
  done
}

say "auth (ephemeral account -> bearer token)"
TOKEN=""
E2E_EMAIL="e2e+$(date +%s)@example.com"
SESSION=$(post /api/auth/signup "{\"email\":\"$E2E_EMAIL\",\"password\":\"e2e-pass-123\"}")
TOKEN=$(echo "$SESSION" | field accessToken)
echo "signed up $E2E_EMAIL (token ${#TOKEN} chars)"
echo "tokenless is rejected: http $(curl -sS -o /dev/null -w '%{http_code}' "$BASE/api/projects") (401 expected)"

say "healthz"
HEALTH=$(get /healthz)
echo "status=$(echo "$HEALTH" | field status) worker=$(echo "$HEALTH" | field worker.resolved) supabase=$(echo "$HEALTH" | field supabase.reachable)"
[[ $(echo "$HEALTH" | field status) == "ok" ]] || { echo "health is degraded — fix before running"; exit 1; }

say "create project (both, maxChunks=2)"
PROJECT=$(post /api/projects "{\"sourceUrl\":\"$VOD\",\"pipeline\":\"both\",\"maxChunks\":2,\"instructions\":\"e2e smoke run\"}")
ID=$(echo "$PROJECT" | field id)
JOB=$(echo "$PROJECT" | field activeJobId)
echo "project $ID job $JOB"

say "worker log stream (job $JOB) until the run ends"
curl -sN -H "Authorization: Bearer $TOKEN" "$BASE/api/jobs/$JOB/logs/stream" \
  | grep --line-buffered '^data:' \
  | sed -u -e 's/^data: //' -e 's/\\"/"/g' \
  | grep --line-buffered -o '"line":"[^"]*"' || true

say "final project state"
wait_status "$ID" ready 120
get "/api/projects/$ID" | python3 -c '
import json, sys
detail = json.load(sys.stdin)
project = detail["project"]
print("status=%s clips=%s chunks=%s longform=%s mirror=%s" % (
    project["status"], project["clipCount"], project["chunkCount"],
    project["longformCount"], detail["hasLocalMirror"]))'

say "clips (short fork, by score)"
get "/api/projects/$ID/clips?pipeline=short&order=score" | python3 -c '
import json, sys
for clip in json.load(sys.stdin):
    print("%s score=%s video=%s vertical=%s" % (
        clip["fileName"], clip["score"], bool(clip["videoUrl"]), bool(clip["verticalUrl"])))'

say "long-form versions + first transcript word"
get "/api/projects/$ID/longform" | python3 -c '
import json, sys
for edit in json.load(sys.stdin):
    print("v%s %ss video=%s" % (edit["version"], edit["durationSeconds"], bool(edit["videoUrl"])))'
get "/api/projects/$ID/transcript?includeWords=true" | python3 -c '
import json, sys
chunks = json.load(sys.stdin)
print("chunks=%s firstWord=%s" % (len(chunks), chunks[0]["words"][0] if chunks and chunks[0]["words"] else None))'

say "revise"
RJOB=$(post "/api/projects/$ID/revise" '{"request":"tighten the opening seconds"}' | field id)
curl -sN -H "Authorization: Bearer $TOKEN" "$BASE/api/jobs/$RJOB/logs/stream" >/dev/null || true
echo "revise job state: $(get "/api/jobs/$RJOB" | field state)"
get "/api/projects/$ID/longform" | python3 -c 'import json,sys; print("versions:", [e["version"] for e in json.load(sys.stdin)])'

say "publish (dry run)"
PJOB=$(post "/api/projects/$ID/publish" '{"target":"longform","platforms":["youtube"],"dryRun":true}' | field id)
curl -sN -H "Authorization: Bearer $TOKEN" "$BASE/api/jobs/$PJOB/logs/stream" >/dev/null || true
get "/api/jobs/$PJOB/logs?tail=15" | python3 -c 'import json,sys; [print(l["line"]) for l in json.load(sys.stdin)]'

say "cancel flow on a second project"
ID2=$(post /api/projects "{\"sourceUrl\":\"$VOD\",\"pipeline\":\"short\",\"maxChunks\":0,\"noResearch\":true,\"noShots\":true,\"noThumbnails\":true}" | field id)
sleep 20
echo "cancel -> $(post "/api/projects/$ID2/cancel" | field status)"
wait_status "$ID2" cancelled 180
echo "re-cancel http $(curl -sS -H "Authorization: Bearer $TOKEN" -o /dev/null -w '%{http_code}' -X POST "$BASE/api/projects/$ID2/cancel") (409 expected)"
echo "cancel ready project http $(curl -sS -H "Authorization: Bearer $TOKEN" -o /dev/null -w '%{http_code}' -X POST "$BASE/api/projects/$ID/cancel") (409 expected)"

say "delete second project + drain cleanup"
curl -sS -H "Authorization: Bearer $TOKEN" -X DELETE "$BASE/api/projects/$ID2" -o /dev/null -w 'delete http %{http_code}\n'
post /api/admin/cleanup '{"limit":100}' | field state
sleep 10
get /healthz | field cleanup.pending

say "done — project $ID kept for inspection"
