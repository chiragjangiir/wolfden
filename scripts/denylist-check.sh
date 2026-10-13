#!/usr/bin/env bash
# Checks the private denylist without ever printing a term. Reports list line
# numbers, and match counts per repo.
#
#   scripts/denylist-check.sh [-l] [repo ...]
#
# The list is the file named by `git config privacy.denylist`. Each repo given
# (default: this one) is scanned for terms that already match a tracked file.
# -l also prints the file:line of each match. Those are paths in your repos,
# so don't share -l output from a private repo.
# Exits 1 if there are issues, 2 on errors.

. "$(git rev-parse --show-toplevel)/.githooks/privacy-lib.sh" || exit 2

locations=0
if [ "${1:-}" = "-l" ]; then locations=1; shift; fi

list=$(git config --get privacy.denylist) || { echo "privacy.denylist is not set" >&2; exit 2; }
[ -f "$list" ] && [ -r "$list" ] || { echo "the denylist file can't be read" >&2; exit 2; }
[ $# -gt 0 ] || set -- "$(git rev-parse --show-toplevel)"
for repo in "$@"; do
  git -C "$repo" rev-parse --git-dir > /dev/null 2>&1 || { echo "not a git repo: $(basename "$repo")" >&2; exit 2; }
done

tmp=$(mktemp -d) || exit 2
trap 'rm -rf "$tmp"' EXIT

# One "line-number<TAB>term" per term, with the same cleaning as the hooks.
privacy_clean_lines < "$list" \
  | awk '!/^#/ && length($0) > 0 { print NR "\t" $0 }' > "$tmp/terms"

# Duplicates, short terms and single words, by line number only.
awk -F '\t' '
  { key = tolower($2)
    if (key in seen) { print "line " $1 ": duplicate of line " seen[key]; issues++ }
    else seen[key] = $1
    if (length($2) < 5) { print "line " $1 ": short (" length($2) " characters); likely to match inside other words"; issues++ }
    else if ($2 ~ /^[A-Za-z]+$/) print "line " $1 ": a single word; make sure it is not a common one"
  }
  END { print issues + 0 > "/dev/stderr" }' "$tmp/terms" 2> "$tmp/issues"
issues=$(cat "$tmp/issues")
terms=$(wc -l < "$tmp/terms" | tr -d ' ')

# Terms that already match a tracked file.
while IFS=$'\t' read -r n t; do
  for repo in "$@"; do
    git -C "$repo" grep -n -I -i -F -e "$t" > "$tmp/hits" 2> /dev/null
    case $? in
      0) ;;
      1) continue ;;
      *) echo "line $n: scan failed in $(basename "$repo")" >&2; exit 2 ;;
    esac
    count=$(wc -l < "$tmp/hits" | tr -d ' ')
    if [ "$locations" = 1 ]; then
      echo "line $n: matches $count place(s) in $(basename "$repo"): $(cut -d: -f1,2 "$tmp/hits" | head -3 | paste -sd ' ' -)"
    else
      echo "line $n: matches $count place(s) in $(basename "$repo")"
    fi
    issues=$((issues + 1))
  done
done < "$tmp/terms"

echo "$terms term(s) checked, $issues issue(s)."
[ "$issues" = 0 ]
