#!/usr/bin/env bash
# Switches CoachBot's LLM to Claude through the Claude Code CLI, signed in with your own Claude subscription.
#
#   ./scripts/use-claude.sh [model]      # model: opus (default), sonnet, or a full model name
#
# 1. Runs `claude setup-token`: your browser opens, you approve, and a one-year token is created.
# 2. Checks the token inside the bot container (Linux, no Keychain: only the token can sign in there).
# 3. Writes Llm__Provider=claude_cli, Llm__Model and CLAUDE_CODE_OAUTH_TOKEN to .env (old file kept as .env.bak).
# 4. Recreates the bot container so it reads .env, and checks Claude answers from inside it.
#
# Run it in your own terminal, not through an AI assistant: `claude setup-token` prints the token on screen. This
# script never prints it, and deletes its record of the sign-in session when it ends. Run it again to renew the token.
set -euo pipefail

cd "$(dirname "$0")/.."
MODEL="${1:-opus}"
ENV_FILE=".env"
CONTAINER="coach-bot-coachbot-1"
CLI_FLAGS=(-p --output-format json --tools "" --safe-mode --restricted --strict-mcp-config --no-session-persistence)

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

command -v claude >/dev/null || fail "The claude CLI is not installed on this machine."
command -v docker >/dev/null || fail "Docker is not available."
[ -f "$ENV_FILE" ] || fail "$ENV_FILE not found (copy .env.example first)."
docker inspect "$CONTAINER" >/dev/null 2>&1 || fail "Container $CONTAINER not found: run 'docker compose up -d' first."

session="$(mktemp -t coachbot-setup-token)"
trap 'rm -P "$session" 2>/dev/null || rm -f "$session"' EXIT

# Asks Claude for "OK" in the bot container; extra docker exec options (e.g. the token to try) come first.
check_in_container() {
  docker exec -i "$@" "$CONTAINER" sh -c 'cd /tmp && claude "$@"' sh "${CLI_FLAGS[@]}" --model "$MODEL" \
    <<<"Reply with the single word OK." 2>/dev/null | grep -q '"is_error":false'
}

say "1/4  Claude sign-in: your browser opens. Approve access with the account the bot should use, then come back here."
script -q "$session" claude setup-token || true

# The token as printed, without terminal colour/cursor codes. A token wrapped across lines is not found and is asked for.
token="$(perl -pe 's/\e\[[0-9;?]*[ -\/]*[@-~]//g; s/\e\][^\a]*(?:\a|\e\\)//g; s/\r//g' "$session" \
  | grep -oE 'sk-ant-oat[0-9]+-[A-Za-z0-9_-]{20,}' | tail -n 1 || true)"

say "2/4  Checking the token inside the bot container…"
if [ -z "$token" ] || ! check_in_container -e "CLAUDE_CODE_OAUTH_TOKEN=$token"; then
  echo "Could not read a working token from the sign-in output."
  read -r -s -p "Paste the token shown above (starts with sk-ant-oat, hidden while typing): " token
  echo
  [ -n "$token" ] || fail "No token given; nothing was changed."
  check_in_container -e "CLAUDE_CODE_OAUTH_TOKEN=$token" || fail "Claude did not answer with that token; nothing was changed."
fi
echo "✓ The token works."

say "3/4  Writing $ENV_FILE (previous version kept as $ENV_FILE.bak)…"
cp -p "$ENV_FILE" "$ENV_FILE.bak"
# Replaces each key's line, or appends it; values go through the environment, never a command line.
KEY_Llm__Provider="claude_cli" KEY_Llm__Model="$MODEL" KEY_CLAUDE_CODE_OAUTH_TOKEN="$token" awk '
  BEGIN { split("Llm__Provider Llm__Model CLAUDE_CODE_OAUTH_TOKEN", keys, " ") }
  {
    for (i in keys) if ($0 ~ "^[[:space:]]*" keys[i] "=") { print keys[i] "=" ENVIRON["KEY_" keys[i]]; done[keys[i]] = 1; next }
    print
  }
  END { for (i in keys) if (!(keys[i] in done)) print keys[i] "=" ENVIRON["KEY_" keys[i]] }
' "$ENV_FILE.bak" > "$ENV_FILE"
unset token
echo "✓ Llm__Provider=claude_cli, Llm__Model=$MODEL, CLAUDE_CODE_OAUTH_TOKEN set."

say "4/4  Restarting the bot with the new settings…"
docker compose up -d >/dev/null
for _ in $(seq 1 45); do
  [ "$(docker inspect -f '{{.State.Health.Status}}' "$CONTAINER" 2>/dev/null)" = "healthy" ] && break
  sleep 2
done
[ "$(docker exec "$CONTAINER" printenv Llm__Provider 2>/dev/null)" = "claude_cli" ] || fail "The container did not pick up the new settings; check 'docker compose logs'."
check_in_container || fail "The bot restarted but Claude does not answer from it; check 'docker compose logs'."

say "✓ Done: the bot now uses Claude ($MODEL). Send /plan in Telegram to build a new plan."
