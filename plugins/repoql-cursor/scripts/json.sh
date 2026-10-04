# JSON for the hooks without jq — sourced, never run. Cursor's payload is read
# by json-leaves.awk, and the reply is written with tr, sed and awk.

# Print "path<TAB>value" for each string leaf of the JSON on stdin whose dotted
# path matches the regex $1. $2 prefixes every printed path.
json_leaves() {
    LC_ALL=C awk -v want="$1" -v prefix="${2:-}" -f "$script_dir/json-leaves.awk"
}

# Print the decoded text of the string at dotted path $1 of the JSON on stdin.
json_string_at() {
    LC_ALL=C awk -v raw="$1" -f "$script_dir/json-leaves.awk"
}

# Print $1 as a JSON string. Control characters JSON cannot carry are dropped,
# and so is a final newline. bash's own substitution is quadratic on old
# versions, so the text streams through tr, sed and awk instead.
json_string() {
    printf '%s' "$1" \
        | LC_ALL=C tr -d '\000-\010\013\014\016-\037\177' \
        | LC_ALL=C sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e $'s/\t/\\\\t/g' -e $'s/\r/\\\\r/g' \
        | LC_ALL=C awk 'BEGIN { printf "\"" } NR > 1 { printf "\\n" } { printf "%s", $0 } END { printf "\"" }'
}
