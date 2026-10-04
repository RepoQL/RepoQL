# Read JSON without jq: print "path<TAB>value" for every leaf whose dotted path
# matches the regex `want` (array elements are numbered from 0), each path
# prefixed with `prefix`. A string prints decoded; true, false and numbers print
# as written; null is absent, so it prints nothing. With `raw` set instead, print the decoded text of every
# string whose path matches that regex, one per line, and nothing else — for
# text that holds newlines, and for JSON carried inside a string.
#
# Records split on the double quote, so the scan is linear in the payload: a
# write hook's payload carries the whole file body, and that body is skipped
# without being copied. Run under LC_ALL=C so bytes pass through untouched.
# Values holding a tab or a newline are not printed; they cannot be a line here.
BEGIN { RS = "\""; depth = 0; in_string = 0 }

function path(    i, p) {
    p = ""
    for (i = 1; i <= depth; i++)
        p = p (i > 1 ? "." : "") (kind[i] == "o" ? key[i] : idx[i])
    return p
}

# Track the containers opened and closed by the text between two strings, and
# print the bare values (true, false, numbers) found there.
function structure(s,    i, c, n, bare) {
    n = length(s)
    bare = ""
    for (i = 1; i <= n; i++) {
        c = substr(s, i, 1)
        if (c ~ /[A-Za-z0-9.+-]/) { bare = bare c; continue }
        if (bare != "") { emit_bare(bare); bare = "" }
        if (c == "{") { depth++; kind[depth] = "o"; expect_key[depth] = 1 }
        else if (c == "[") { depth++; kind[depth] = "a"; idx[depth] = 0 }
        else if (c == "}" || c == "]") depth--
        else if (c == ":") expect_key[depth] = 0
        else if (c == ",") { if (kind[depth] == "a") idx[depth]++; else expect_key[depth] = 1 }
    }
    if (bare != "") emit_bare(bare)
}

function emit_bare(bare) {
    if (raw == "" && bare != "null" && path() ~ want) print prefix path() "\t" bare
}

# Decode a JSON string body. Splitting on the backslash keeps this linear: an
# empty part is an escaped backslash, and the part after it is literal text.
function decode(s,    n, parts, i, out, p, c, code) {
    if (index(s, "\\") == 0) return s
    n = split(s, parts, /[\\]/)
    out = parts[1]
    for (i = 2; i <= n; i++) {
        p = parts[i]
        if (p == "") { out = out "\\"; if (++i <= n) out = out parts[i]; continue }
        c = substr(p, 1, 1); p = substr(p, 2)
        if (c == "n") out = out "\n" p
        else if (c == "t") out = out "\t" p
        else if (c == "r") out = out "\r" p
        else if (c == "b") out = out "\b" p
        else if (c == "f") out = out "\f" p
        else if (c == "u") {
            code = hex(substr(p, 1, 4)); p = substr(p, 5)
            # A high surrogate and the low surrogate after it name one character.
            if (code >= 55296 && code < 56320 && p == "" && i < n && substr(parts[i + 1], 1, 1) == "u") {
                i++
                code = 65536 + (code - 55296) * 1024 + hex(substr(parts[i], 2, 4)) - 56320
                p = substr(parts[i], 6)
            }
            out = out utf8(code) p
        }
        else out = out c p
    }
    return out
}

# Some awks print %c as one byte and others as the UTF-8 for a code point.
function utf8(code) {
    if (code < 128 || sprintf("%c", 233) == "\303\251") return sprintf("%c", code)
    if (code < 2048) return sprintf("%c%c", 192 + int(code / 64), 128 + code % 64)
    if (code < 65536) return sprintf("%c%c%c", 224 + int(code / 4096), 128 + int(code / 64) % 64, 128 + code % 64)
    return sprintf("%c%c%c%c", 240 + int(code / 262144), 128 + int(code / 4096) % 64, 128 + int(code / 64) % 64, 128 + code % 64)
}

function hex(digits,    i, value) {
    value = 0
    digits = tolower(digits)
    for (i = 1; i <= length(digits); i++)
        value = value * 16 + index("0123456789abcdef", substr(digits, i, 1)) - 1
    return value
}

!in_string {
    structure($0)
    in_string = 1; body = ""
    is_key = (depth > 0 && kind[depth] == "o" && expect_key[depth])
    here = is_key ? "" : path()
    keep = is_key || here ~ (raw != "" ? raw : want)
    next
}

{
    # An odd run of backslashes at the end means the quote that closed this
    # record was escaped, and the string continues in the next record.
    continues = (match($0, /[\\]+$/) && RLENGTH % 2)
    if (raw != "" && keep && !is_key) {
        if (!started) { if (printed++) printf "\n"; started = 1 }
        printf "%s", decode(continues ? substr($0, 1, length($0) - 1) : $0)
        if (continues) { printf "\""; next }
        started = 0; in_string = 0
        next
    }
    if (keep) body = body $0 (continues ? "\"" : "")
    if (continues) next
    in_string = 0
    if (is_key) key[depth] = decode(body)
    else if (keep) {
        value = decode(body)
        if (value !~ /[\t\n]/) print prefix here "\t" value
    }
}
