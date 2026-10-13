# Shared helpers for the privacy hooks. Sourced, not run.
#
# The denylist is a private file outside the repo, named by
# `git config privacy.denylist`. One term per line, matched case-insensitively
# as a fixed string; blank lines and lines starting with # are ignored.
# Matches are reported by location only, so terms never appear in output.

# Personal user paths. Keep in sync with .github/workflows/privacy-check.yml.
# In docs, write %USERPROFILE% or ~ instead.
PRIVACY_PATH_RE='[A-Za-z]:[\/]Users[\/]|/[cC]/Users/'

# Reads a denylist on stdin and strips a UTF-8 BOM and the whitespace around
# each line, which also removes a CR. Keeps every line, so line numbers hold.
# Portable sed only: BSD sed has no \x escapes.
privacy_clean_lines() {
  sed -e "1s/^$(printf '\357\273\277')//" -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//'
}

# Reads a denylist on stdin and writes only the terms: no comments or blanks.
privacy_clean_terms() {
  privacy_clean_lines | sed -e '/^#/d' -e '/^$/d'
}

# Writes the denylist's terms to the file named by $1.
# Returns 0 if loaded (the file may hold no terms), 1 if no list is
# configured, and 2 if one is configured but can't be read. Callers block on 2.
privacy_load_denylist() {
  list=$(git config --get privacy.denylist) || return 1
  if [ ! -f "$list" ] || [ ! -r "$list" ]; then
    echo "privacy: the denylist named by git config privacy.denylist can't be read." >&2
    return 2
  fi
  privacy_clean_terms < "$list" > "$1" || return 2
}
