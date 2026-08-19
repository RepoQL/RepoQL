#!/bin/bash
# Surface on-disk concepts relevant to every file in a Codex apply_patch call.
# Fail open: malformed concepts or unavailable tooling must never block an edit.
trap 'exit 0' ERR

command -v jq >/dev/null 2>&1 || exit 0

input=$(cat)
patch=$(jq -r '.tool_input.command // .tool_input.patch // empty' <<<"$input")
session=$(jq -r '.session_id // .turn_id // empty' <<<"$input")
workspace=$(jq -r '.cwd // empty' <<<"$input")
[ -n "$patch" ] || exit 0
[ -d "$workspace" ] && cd "$workspace"

files=$(printf '%s\n' "$patch" \
    | sed -nE 's/^\*\*\* (Update|Add|Delete) File: //p' \
    | awk '!seen[$0]++' \
    | head -8)
[ -n "$files" ] || exit 0

concept_root="$workspace/.repoql/concepts"
[ -d "$concept_root" ] || exit 0

# Normalize every changed path to the file:/// URI shape used by relevance
# globs in concept frontmatter.
target_uris=""
while IFS= read -r file; do
    [ -n "$file" ] || continue
    case "$file" in
        file://*) target_uri="$file" ;;
        "$workspace"/*) target_uri="file:///${file#"$workspace"/}" ;;
        *) target_uri="file:///${file#./}" ;;
    esac
    target_uris+="$target_uri"$'\n'
done < <(printf '%s\n' "$files")

matches_relevance() {
    local relevance="$1"
    local raw pattern target matched=0
    local -a patterns
    IFS=';' read -r -a patterns <<<"$relevance"

    for raw in "${patterns[@]}"; do
        pattern=$(printf '%s' "$raw" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
        [ -n "$pattern" ] || continue
        negative=""
        if [[ "$pattern" == !* ]]; then
            negative=1
            pattern="${pattern#!}"
        fi
        [[ "$pattern" == *://* ]] || pattern="file:///${pattern#./}"

        while IFS= read -r target; do
            [ -n "$target" ] || continue
            if [[ "$target" == $pattern ]]; then
                [ -n "$negative" ] && return 1
                matched=1
            fi
        done <<<"$target_uris"
    done

    [ "$matched" -eq 1 ]
}

plugin_data="${PLUGIN_DATA:-${CLAUDE_PLUGIN_DATA:-${TMPDIR:-/tmp}/repoql-codex}}"
session_key=$(printf '%s' "${session:-ambient}" | tr -c 'A-Za-z0-9._-' '_')
seen_dir="$plugin_data/concept-hints"
mkdir -p "$seen_dir" 2>/dev/null || exit 0
seen_file="$seen_dir/$session_key.seen"
touch "$seen_file" 2>/dev/null || exit 0

context=""
count=0
while IFS= read -r concept_file; do
    [ "$count" -ge 5 ] && break
    concept_key="${concept_file#"$concept_root"/}"
    grep -Fqx -- "$concept_key" "$seen_file" 2>/dev/null && continue

    relevance=$(awk '
        NR == 1 && $0 == "---" { frontmatter=1; next }
        frontmatter && $0 == "---" { exit }
        frontmatter && /^relevance:[[:space:]]*/ {
            sub(/^relevance:[[:space:]]*/, "")
            gsub(/^"|"$/, "")
            print
            exit
        }
    ' "$concept_file")
    [ -n "$relevance" ] || continue
    matches_relevance "$relevance" || continue

    invariant=$(awk '
        /^\*\*Invariant\*\*[[:space:]]*$/ { capture=1; next }
        capture && $0 !~ /^[[:space:]]*$/ { print; exit }
    ' "$concept_file")
    [ -n "$invariant" ] || continue

    why=$(awk '
        /^\*\*Why\*\*[[:space:]]*$/ { capture=1; next }
        capture && /^\*\*/ { exit }
        capture && /^##[[:space:]]/ { exit }
        capture {
            if (started || $0 !~ /^[[:space:]]*$/) {
                print
                started=1
            }
        }
    ' "$concept_file")

    entry="concept:///$concept_key"$'\t'"$invariant"
    if [ -n "$why" ]; then
        why=$(printf '%s\n' "$why" | sed 's/^/  /')
        entry+=$'\n'"  why:"$'\n'"$why"
    fi
    if [ -n "$context" ]; then
        context+=$'\n\n'
    fi
    context+="$entry"
    printf '%s\n' "$concept_key" >>"$seen_file"
    count=$((count + 1))
done < <(find "$concept_root" -type f -name '*.md' ! -iname 'readme.md' -print 2>/dev/null | sort)

[ -n "$context" ] || exit 0
jq -n --arg ctx "$context" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    additionalContext: $ctx
  }
}'
exit 0
