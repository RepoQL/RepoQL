#!/bin/bash
# Checks each Claude Code plugin against the rules the Claude plugin directory's
# validation enforces and that `claude plugin validate` does not check:
# https://claude.com/docs/plugins/pre-submission-checklist
# Each failure is printed with its plugin and the rule it breaks; the exit
# status is the number of failures, capped at 1.
#
# Usage: .github/scripts/directory-readiness.sh [plugin-dir ...]
# With no arguments it checks every plugin listed in .claude-plugin/marketplace.json.
set -u

failures=0
fail() { echo "FAIL $1: $2"; failures=$((failures + 1)); }

if [ "$#" -eq 0 ]; then
    set -- $(grep -oE '"source": *"\./[^"]+"' .claude-plugin/marketplace.json | sed -E 's/.*"\.\/([^"]+)"/\1/')
fi

for plugin in "$@"; do
    plugin=${plugin%/}
    [ -f "$plugin/.claude-plugin/plugin.json" ] || { fail "$plugin" "no .claude-plugin/plugin.json"; continue; }

    # README of at least 40 words outside fenced code blocks.
    if [ -f "$plugin/README.md" ]; then
        words=$(awk '/^```/ { fenced = !fenced; next } !fenced' "$plugin/README.md" | wc -w | tr -d ' ')
        [ "$words" -ge 40 ] || fail "$plugin" "README.md has $words words outside code blocks; the directory needs 40"
    else
        fail "$plugin" "no README.md"
    fi

    # A LICENSE file in the folder: installs receive only the plugin folder.
    ls "$plugin"/LICENSE* >/dev/null 2>&1 || fail "$plugin" "no LICENSE file in the plugin folder"

    # Regular files only, and no operating-system junk.
    while IFS= read -r entry; do fail "$plugin" "symbolic link: $entry"; done < <(find "$plugin" -type l)
    while IFS= read -r entry; do fail "$plugin" "system file: $entry"; done < <(find "$plugin" \( -name .DS_Store -o -name Thumbs.db -o -name desktop.ini -o -name __MACOSX \))

    # At most 512 files; non-image files under 256 KiB; nothing binary but images and fonts.
    count=$(find "$plugin" -type f | wc -l | tr -d ' ')
    [ "$count" -le 512 ] || fail "$plugin" "$count files; the directory holds a plugin with more than 512 for review"
    while IFS= read -r file; do
        case "$file" in
            *.png|*.jpg|*.jpeg|*.gif|*.webp|*.woff|*.woff2|*.ttf|*.otf) continue ;;
        esac
        size=$(wc -c <"$file" | tr -d ' ')
        [ "$size" -lt 262144 ] || fail "$plugin" "$file is $size bytes; non-image files must be under 256 KiB"
        # `file` calls an empty file binary; it is not.
        [ "$size" -gt 0 ] || continue
        case "$(file -b --mime-encoding "$file")" in
            binary) fail "$plugin" "$file is binary; only text, images and fonts pass without review" ;;
        esac
    done < <(find "$plugin" -type f)
done

[ "$failures" -eq 0 ] && echo "Directory readiness: $# plugin(s) pass" && exit 0
echo "Directory readiness: $failures failure(s)"
exit 1
