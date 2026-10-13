# Shared helpers for the privacy hooks. Sourced, not run.
#
# The denylist is a private file outside the repo, named by
# `git config privacy.denylist`. One term per line, matched case-insensitively
# as a fixed string; blank lines and lines starting with # are ignored.
# Matches are reported by location only, so terms never appear in output.

# Personal user paths. In docs, write %USERPROFILE% or ~ instead.
PRIVACY_PATH_RE='[A-Za-z]:[\/]Users[\/]|/[cC]/Users/'

# Writes the cleaned denylist (CR, comments, blank lines and surrounding
# whitespace removed) to the file named by $1. Returns 1 if no list is set up.
privacy_load_denylist() {
  list=$(git config --get privacy.denylist) || return 1
  [ -f "$list" ] || { echo "privacy: denylist file not found (git config privacy.denylist)" >&2; return 1; }
  tr -d '\r' < "$list" \
    | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e '/^#/d' -e '/^$/d' > "$1"
}
