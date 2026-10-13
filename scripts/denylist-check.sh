#!/usr/bin/env bash
# Checks the private denylist without ever printing a term. Reports list line
# numbers only, so the output is safe to share.
#
#   scripts/denylist-check.sh [repo ...]
#
# The list is the file named by `git config privacy.denylist`. Each repo given
# (default: this one) is scanned for terms that already match a tracked file.

set -u
. "$(git rev-parse --show-toplevel)/.githooks/privacy-lib.sh"

list=$(git config --get privacy.denylist) || { echo "privacy.denylist is not set" >&2; exit 1; }
[ -f "$list" ] || { echo "denylist file not found" >&2; exit 1; }
[ $# -gt 0 ] || set -- "$(git rev-parse --show-toplevel)"

terms=0
issues=0
declare -A seen
n=0
while IFS= read -r raw || [ -n "$raw" ]; do
  n=$((n + 1))
  t=$(printf '%s' "$raw" | tr -d '\r' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
  case "$t" in ''|'#'*) continue ;; esac
  terms=$((terms + 1))
  key=$(printf '%s' "$t" | tr '[:upper:]' '[:lower:]')

  if [ -n "${seen[$key]:-}" ]; then
    echo "line $n: duplicate of line ${seen[$key]}"; issues=$((issues + 1))
  else
    seen[$key]=$n
  fi
  if [ ${#t} -lt 5 ]; then
    echo "line $n: short (${#t} characters); likely to match inside other words"; issues=$((issues + 1))
  elif printf '%s' "$t" | grep -q -E '^[A-Za-z]+$'; then
    echo "line $n: a single word; make sure it isn't a common one"
  fi
  if [ "$t" != "$(printf '%s' "$raw" | tr -d '\r')" ]; then
    echo "line $n: note: leading or trailing spaces are ignored"
  fi

  for repo in "$@"; do
    hits=$(git -C "$repo" grep -n -I -i -F -e "$t" 2>/dev/null | cut -d: -f1,2)
    if [ -n "$hits" ]; then
      count=$(printf '%s\n' "$hits" | wc -l | tr -d ' ')
      first=$(printf '%s\n' "$hits" | head -3 | paste -sd ' ' -)
      echo "line $n: matches $count place(s) in $(basename "$repo"): $first"
      issues=$((issues + 1))
    fi
  done
done < "$list"

echo "$terms term(s) checked, $issues issue(s)."
