#include "quickjs.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

__attribute__((import_module("env"), import_name("repoql_log")))
extern void repoql_log(int32_t level, const char* msg, int32_t msg_len);

__attribute__((import_module("env"), import_name("repoql_query")))
extern int64_t repoql_query(const char* sql, int32_t sql_len);

__attribute__((import_module("env"), import_name("repoql_read")))
extern int64_t repoql_read(const char* uri, int32_t uri_len, int32_t budget);

__attribute__((import_module("env"), import_name("repoql_load_module")))
extern int64_t repoql_load_module(const char* specifier, int32_t specifier_len);

__attribute__((import_module("env"), import_name("repoql_write")))
extern int64_t repoql_write(const char* uri, int32_t uri_len, const char* content, int32_t content_len);

__attribute__((import_module("env"), import_name("repoql_delete")))
extern int64_t repoql_delete(const char* uri, int32_t uri_len);

__attribute__((import_module("env"), import_name("repoql_ffmpeg")))
extern int64_t repoql_ffmpeg(const char* json, int32_t json_len);

__attribute__((import_module("env"), import_name("repoql_graphviz")))
extern int64_t repoql_graphviz(const char* dot, int32_t dot_len,
                                const char* engine, int32_t engine_len,
                                const char* format, int32_t format_len);

__attribute__((import_module("env"), import_name("repoql_pandoc")))
extern int64_t repoql_pandoc(const char* json, int32_t json_len);

__attribute__((import_module("env"), import_name("repoql_svg_to_png")))
extern int64_t repoql_svg_to_png(const char* svg, int32_t svg_len, int32_t width);

static const char* kErrorSuggestion = "Check your JavaScript syntax and logic";
static const char* kInitFailureJson =
    "{\"error\":{\"kind\":\"runtime\",\"message\":\"Failed to initialize QuickJS runtime\","
    "\"suggestion\":\"Check your JavaScript syntax and logic\"}}";
static const char* kSerializationFailureJson =
    "{\"error\":{\"kind\":\"runtime\",\"message\":\"Failed to serialize evaluation result\","
    "\"suggestion\":\"Check your JavaScript syntax and logic\"}}";
static const char* kAllocationFailureJson =
    "{\"error\":{\"kind\":\"runtime\",\"message\":\"Failed to allocate result buffer\","
    "\"suggestion\":\"Check your JavaScript syntax and logic\"}}";
static const char* kInvalidArgumentsJson =
    "{\"error\":{\"kind\":\"runtime\",\"message\":\"Invalid evaluator arguments\","
    "\"suggestion\":\"Check your JavaScript syntax and logic\"}}";

static int64_t pack_result(char* result_ptr, int32_t result_len) {
    return ((int64_t)(uint32_t)(uintptr_t)result_ptr << 32) | (uint32_t)result_len;
}

static int64_t pack_bytes(const char* bytes, size_t len) {
    char* result = (char*)malloc(len + 1);
    if (result == NULL) {
        return 0;
    }

    if (len > 0) {
        memcpy(result, bytes, len);
    }

    result[len] = '\0';
    return pack_result(result, (int32_t)len);
}

static int64_t pack_cstring(const char* text) {
    return pack_bytes(text, strlen(text));
}

static int append_console_part(char** buffer, size_t* capacity, size_t* used, const char* text, size_t text_len) {
    size_t separator_len = (*used == 0) ? 0 : 1;
    size_t required = *used + separator_len + text_len + 1;

    if (required > *capacity) {
        size_t new_capacity = (*capacity == 0) ? 64 : *capacity;
        while (new_capacity < required) {
            new_capacity *= 2;
        }

        char* resized = (char*)realloc(*buffer, new_capacity);
        if (resized == NULL) {
            return -1;
        }

        *buffer = resized;
        *capacity = new_capacity;
    }

    if (separator_len != 0) {
        (*buffer)[(*used)++] = ' ';
    }

    if (text_len > 0) {
        memcpy(*buffer + *used, text, text_len);
        *used += text_len;
    }

    (*buffer)[*used] = '\0';
    return 0;
}

static JSValue console_write(JSContext* ctx, int32_t level, int argc, JSValueConst* argv) {
    char* buffer = NULL;
    size_t capacity = 0;
    size_t used = 0;

    for (int i = 0; i < argc; i++) {
        JSValue string_value = JS_ToString(ctx, argv[i]);
        if (JS_IsException(string_value)) {
            free(buffer);
            return JS_EXCEPTION;
        }

        size_t part_len = 0;
        const char* part = JS_ToCStringLen(ctx, &part_len, string_value);
        if (part == NULL) {
            JS_FreeValue(ctx, string_value);
            free(buffer);
            return JS_EXCEPTION;
        }

        if (append_console_part(&buffer, &capacity, &used, part, part_len) < 0) {
            JS_FreeCString(ctx, part);
            JS_FreeValue(ctx, string_value);
            free(buffer);
            return JS_ThrowOutOfMemory(ctx);
        }

        JS_FreeCString(ctx, part);
        JS_FreeValue(ctx, string_value);
    }

    repoql_log(level, buffer != NULL ? buffer : "", (int32_t)used);
    free(buffer);
    return JS_UNDEFINED;
}

static JSValue js_console_log(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    return console_write(ctx, 0, argc, argv);
}

static JSValue js_console_warn(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    return console_write(ctx, 1, argc, argv);
}

static JSValue js_console_error(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    return console_write(ctx, 2, argc, argv);
}

static int install_console(JSContext* ctx) {
    JSValue global_object = JS_GetGlobalObject(ctx);
    JSValue console_object = JS_NewObject(ctx);

    if (JS_IsException(global_object) || JS_IsException(console_object)) {
        JS_FreeValue(ctx, global_object);
        JS_FreeValue(ctx, console_object);
        return -1;
    }

    if (JS_SetPropertyStr(ctx, console_object, "log", JS_NewCFunction(ctx, js_console_log, "log", 1)) < 0 ||
        JS_SetPropertyStr(ctx, console_object, "warn", JS_NewCFunction(ctx, js_console_warn, "warn", 1)) < 0 ||
        JS_SetPropertyStr(ctx, console_object, "error", JS_NewCFunction(ctx, js_console_error, "error", 1)) < 0 ||
        JS_SetPropertyStr(ctx, global_object, "console", console_object) < 0) {
        JS_FreeValue(ctx, global_object);
        return -1;
    }

    JS_FreeValue(ctx, global_object);
    return 0;
}

static JSValue js_repoql_query(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 1) {
        return JS_ThrowTypeError(ctx, "repoql.query requires a SQL string argument");
    }

    size_t sql_len = 0;
    const char* sql = JS_ToCStringLen(ctx, &sql_len, argv[0]);
    if (sql == NULL) {
        return JS_EXCEPTION;
    }

    int64_t packed = repoql_query(sql, (int32_t)sql_len);
    JS_FreeCString(ctx, sql);

    int32_t result_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t result_len = (int32_t)(packed & 0xFFFFFFFF);

    if (result_ptr == 0 || result_len <= 0) {
        return JS_ThrowInternalError(ctx, "query returned no result");
    }

    const char* result_json = (const char*)(uintptr_t)result_ptr;
    JSValue parsed = JS_ParseJSON(ctx, result_json, (size_t)result_len, "<query>");
    free((void*)(uintptr_t)result_ptr);

    if (JS_IsException(parsed)) {
        return JS_EXCEPTION;
    }

    JSValue query_error = JS_GetPropertyStr(ctx, parsed, "__repoqlQueryError");
    if (JS_IsException(query_error)) {
        JS_FreeValue(ctx, parsed);
        return JS_EXCEPTION;
    }

    if (!JS_IsUndefined(query_error) && !JS_IsNull(query_error)) {
        const char* message = JS_ToCString(ctx, query_error);
        JS_FreeValue(ctx, query_error);
        JS_FreeValue(ctx, parsed);
        if (message == NULL) {
            return JS_EXCEPTION;
        }

        JSValue exception = JS_ThrowInternalError(ctx, "%s", message);
        JS_FreeCString(ctx, message);
        return exception;
    }

    JS_FreeValue(ctx, query_error);
    return parsed;
}

static JSValue js_repoql_read(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 1) {
        return JS_ThrowTypeError(ctx, "repoql.read requires a URI string argument");
    }

    size_t uri_len = 0;
    const char* uri = JS_ToCStringLen(ctx, &uri_len, argv[0]);
    if (uri == NULL) {
        return JS_EXCEPTION;
    }

    int32_t budget = 5000;
    if (argc >= 2 && JS_IsObject(argv[1])) {
        JSValue budget_value = JS_GetPropertyStr(ctx, argv[1], "budget");
        if (JS_IsException(budget_value)) {
            JS_FreeCString(ctx, uri);
            return JS_EXCEPTION;
        }

        if (!JS_IsUndefined(budget_value) && !JS_IsNull(budget_value)) {
            if (JS_ToInt32(ctx, &budget, budget_value) < 0) {
                JS_FreeValue(ctx, budget_value);
                JS_FreeCString(ctx, uri);
                return JS_EXCEPTION;
            }
        }

        JS_FreeValue(ctx, budget_value);
    }

    int64_t packed = repoql_read(uri, (int32_t)uri_len, budget);
    JS_FreeCString(ctx, uri);

    int32_t result_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t result_len = (int32_t)(packed & 0xFFFFFFFF);

    if (result_ptr == 0 || result_len <= 0) {
        return JS_ThrowInternalError(ctx, "read returned no result");
    }

    const char* result_json = (const char*)(uintptr_t)result_ptr;
    JSValue parsed = JS_ParseJSON(ctx, result_json, (size_t)result_len, "<read>");
    free((void*)(uintptr_t)result_ptr);

    if (JS_IsException(parsed)) {
        return JS_EXCEPTION;
    }

    JSValue read_error = JS_GetPropertyStr(ctx, parsed, "__repoqlReadError");
    if (JS_IsException(read_error)) {
        JS_FreeValue(ctx, parsed);
        return JS_EXCEPTION;
    }

    if (!JS_IsUndefined(read_error) && !JS_IsNull(read_error)) {
        const char* message = JS_ToCString(ctx, read_error);
        JS_FreeValue(ctx, read_error);
        JS_FreeValue(ctx, parsed);
        if (message == NULL) {
            return JS_EXCEPTION;
        }

        JSValue exception = JS_ThrowInternalError(ctx, "%s", message);
        JS_FreeCString(ctx, message);
        return exception;
    }

    JS_FreeValue(ctx, read_error);
    return parsed;
}

static JSValue throw_packed_host_error(JSContext* ctx, int64_t packed, const char* operation) {
    int32_t error_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t error_len = (int32_t)(packed & 0xFFFFFFFF);

    if (error_ptr == 0 || error_len <= 0) {
        return JS_ThrowInternalError(ctx, "%s returned an invalid error", operation);
    }

    const char* error_message = (const char*)(uintptr_t)error_ptr;
    JSValue exception = JS_ThrowInternalError(ctx, "%.*s", error_len, error_message);
    free((void*)(uintptr_t)error_ptr);
    return exception;
}

static JSValue js_repoql_write(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 2) {
        return JS_ThrowTypeError(ctx, "repoql.write requires URI and content string arguments");
    }

    size_t uri_len = 0;
    const char* uri = JS_ToCStringLen(ctx, &uri_len, argv[0]);
    if (uri == NULL) {
        return JS_EXCEPTION;
    }

    size_t content_len = 0;
    const char* content = JS_ToCStringLen(ctx, &content_len, argv[1]);
    if (content == NULL) {
        JS_FreeCString(ctx, uri);
        return JS_EXCEPTION;
    }

    int64_t result = repoql_write(uri, (int32_t)uri_len, content, (int32_t)content_len);
    JS_FreeCString(ctx, content);
    JS_FreeCString(ctx, uri);

    if (result == 0) {
        return JS_UNDEFINED;
    }

    return throw_packed_host_error(ctx, result, "write");
}

static JSValue js_repoql_delete(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 1) {
        return JS_ThrowTypeError(ctx, "repoql.delete requires a URI string argument");
    }

    size_t uri_len = 0;
    const char* uri = JS_ToCStringLen(ctx, &uri_len, argv[0]);
    if (uri == NULL) {
        return JS_EXCEPTION;
    }

    int64_t result = repoql_delete(uri, (int32_t)uri_len);
    JS_FreeCString(ctx, uri);

    if (result == 0) {
        return JS_UNDEFINED;
    }

    return throw_packed_host_error(ctx, result, "delete");
}

static JSValue js_repoql_ffmpeg(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 1 || !JS_IsObject(argv[0])) {
        return JS_ThrowTypeError(ctx, "repoql.ffmpeg requires an options object argument");
    }

    JSValue json_value = JS_JSONStringify(ctx, argv[0], JS_UNDEFINED, JS_UNDEFINED);
    if (JS_IsException(json_value)) {
        return JS_EXCEPTION;
    }

    size_t json_len = 0;
    const char* json = JS_ToCStringLen(ctx, &json_len, json_value);
    JS_FreeValue(ctx, json_value);
    if (json == NULL) {
        return JS_EXCEPTION;
    }

    int64_t packed = repoql_ffmpeg(json, (int32_t)json_len);
    JS_FreeCString(ctx, json);

    int32_t result_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t result_len = (int32_t)(packed & 0xFFFFFFFF);

    if (result_ptr == 0 || result_len <= 0) {
        return JS_ThrowInternalError(ctx, "ffmpeg returned no result");
    }

    const char* result_json = (const char*)(uintptr_t)result_ptr;
    JSValue parsed = JS_ParseJSON(ctx, result_json, (size_t)result_len, "<ffmpeg>");
    free((void*)(uintptr_t)result_ptr);

    if (JS_IsException(parsed)) {
        return JS_EXCEPTION;
    }

    JSValue ffmpeg_error = JS_GetPropertyStr(ctx, parsed, "__repoqlFfmpegError");
    if (JS_IsException(ffmpeg_error)) {
        JS_FreeValue(ctx, parsed);
        return JS_EXCEPTION;
    }

    if (!JS_IsUndefined(ffmpeg_error) && !JS_IsNull(ffmpeg_error)) {
        const char* message = JS_ToCString(ctx, ffmpeg_error);
        JS_FreeValue(ctx, ffmpeg_error);
        JS_FreeValue(ctx, parsed);
        if (message == NULL) {
            return JS_EXCEPTION;
        }
        JSValue exception = JS_ThrowInternalError(ctx, "%s", message);
        JS_FreeCString(ctx, message);
        return exception;
    }

    JS_FreeValue(ctx, ffmpeg_error);
    return parsed;
}

static JSValue js_repoql_graphviz(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 1) {
        return JS_ThrowTypeError(ctx, "repoql.graphviz requires a DOT string argument");
    }

    /* Extract DOT source (required) */
    size_t dot_len = 0;
    const char* dot = JS_ToCStringLen(ctx, &dot_len, argv[0]);
    if (dot == NULL) {
        return JS_EXCEPTION;
    }

    /* Extract engine (optional, default "dot") */
    const char* engine = "dot";
    size_t engine_len = 3;
    const char* engine_alloc = NULL;
    if (argc >= 2 && !JS_IsUndefined(argv[1]) && !JS_IsNull(argv[1])) {
        engine_alloc = JS_ToCStringLen(ctx, &engine_len, argv[1]);
        if (engine_alloc == NULL) {
            JS_FreeCString(ctx, dot);
            return JS_EXCEPTION;
        }
        engine = engine_alloc;
    }

    /* Extract format (optional, default "svg") */
    const char* format = "svg";
    size_t format_len = 3;
    const char* format_alloc = NULL;
    if (argc >= 3 && !JS_IsUndefined(argv[2]) && !JS_IsNull(argv[2])) {
        format_alloc = JS_ToCStringLen(ctx, &format_len, argv[2]);
        if (format_alloc == NULL) {
            JS_FreeCString(ctx, engine_alloc);
            JS_FreeCString(ctx, dot);
            return JS_EXCEPTION;
        }
        format = format_alloc;
    }

    int64_t packed = repoql_graphviz(dot, (int32_t)dot_len,
                                      engine, (int32_t)engine_len,
                                      format, (int32_t)format_len);
    JS_FreeCString(ctx, format_alloc);
    JS_FreeCString(ctx, engine_alloc);
    JS_FreeCString(ctx, dot);

    int32_t result_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t result_len = (int32_t)(packed & 0xFFFFFFFF);

    if (result_ptr == 0 || result_len <= 0) {
        return JS_ThrowInternalError(ctx, "graphviz returned no result");
    }

    const char* result_str = (const char*)(uintptr_t)result_ptr;

    /* Check for JSON error response */
    if (result_len > 5 && result_str[0] == '{' && result_str[1] == '"') {
        JSValue parsed = JS_ParseJSON(ctx, result_str, (size_t)result_len, "<graphviz>");
        free((void*)(uintptr_t)result_ptr);
        if (JS_IsException(parsed)) {
            return JS_EXCEPTION;
        }
        JSValue gv_error = JS_GetPropertyStr(ctx, parsed, "__repoqlGraphvizError");
        if (!JS_IsException(gv_error) && !JS_IsUndefined(gv_error) && !JS_IsNull(gv_error)) {
            const char* message = JS_ToCString(ctx, gv_error);
            JS_FreeValue(ctx, gv_error);
            JS_FreeValue(ctx, parsed);
            if (message == NULL) return JS_EXCEPTION;
            JSValue exception = JS_ThrowInternalError(ctx, "%s", message);
            JS_FreeCString(ctx, message);
            return exception;
        }
        JS_FreeValue(ctx, gv_error);
        JS_FreeValue(ctx, parsed);
    }

    /* Return the raw SVG/output as a string */
    JSValue result = JS_NewStringLen(ctx, result_str, (size_t)result_len);
    free((void*)(uintptr_t)result_ptr);
    return result;
}

static JSValue js_repoql_pandoc(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 1 || !JS_IsObject(argv[0])) {
        return JS_ThrowTypeError(ctx, "repoql.pandoc requires an options object argument");
    }

    JSValue json_value = JS_JSONStringify(ctx, argv[0], JS_UNDEFINED, JS_UNDEFINED);
    if (JS_IsException(json_value)) {
        return JS_EXCEPTION;
    }

    size_t json_len = 0;
    const char* json = JS_ToCStringLen(ctx, &json_len, json_value);
    JS_FreeValue(ctx, json_value);
    if (json == NULL) {
        return JS_EXCEPTION;
    }

    int64_t packed = repoql_pandoc(json, (int32_t)json_len);
    JS_FreeCString(ctx, json);

    int32_t result_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t result_len = (int32_t)(packed & 0xFFFFFFFF);

    if (result_ptr == 0 || result_len <= 0) {
        return JS_ThrowInternalError(ctx, "pandoc returned no result");
    }

    const char* result_json = (const char*)(uintptr_t)result_ptr;
    JSValue parsed = JS_ParseJSON(ctx, result_json, (size_t)result_len, "<pandoc>");
    free((void*)(uintptr_t)result_ptr);

    if (JS_IsException(parsed)) {
        return JS_EXCEPTION;
    }

    JSValue pandoc_error = JS_GetPropertyStr(ctx, parsed, "__repoqlPandocError");
    if (JS_IsException(pandoc_error)) {
        JS_FreeValue(ctx, parsed);
        return JS_EXCEPTION;
    }

    if (!JS_IsUndefined(pandoc_error) && !JS_IsNull(pandoc_error)) {
        const char* message = JS_ToCString(ctx, pandoc_error);
        JS_FreeValue(ctx, pandoc_error);
        JS_FreeValue(ctx, parsed);
        if (message == NULL) return JS_EXCEPTION;
        JSValue exception = JS_ThrowInternalError(ctx, "%s", message);
        JS_FreeCString(ctx, message);
        return exception;
    }

    JS_FreeValue(ctx, pandoc_error);
    return parsed;
}

static JSValue js_repoql_svg_to_png(JSContext* ctx, JSValueConst this_val, int argc, JSValueConst* argv) {
    (void)this_val;
    if (argc < 1) {
        return JS_ThrowTypeError(ctx, "repoql.svgToPng requires an SVG string argument");
    }

    size_t svg_len = 0;
    const char* svg = JS_ToCStringLen(ctx, &svg_len, argv[0]);
    if (svg == NULL) {
        return JS_EXCEPTION;
    }

    int32_t width = 0;
    if (argc >= 2 && !JS_IsUndefined(argv[1]) && !JS_IsNull(argv[1])) {
        if (JS_ToInt32(ctx, &width, argv[1]) < 0) {
            JS_FreeCString(ctx, svg);
            return JS_EXCEPTION;
        }
    }

    int64_t packed = repoql_svg_to_png(svg, (int32_t)svg_len, width);
    JS_FreeCString(ctx, svg);

    int32_t result_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t result_len = (int32_t)(packed & 0xFFFFFFFF);

    if (result_ptr == 0 || result_len <= 0) {
        return JS_ThrowInternalError(ctx, "SVG to PNG rendering failed");
    }

    /* Result is a base64 string or error JSON from the host */
    const char* result_str = (const char*)(uintptr_t)result_ptr;

    /* Check for error */
    if (result_len > 5 && result_str[0] == '{' && result_str[1] == '"') {
        JSValue parsed = JS_ParseJSON(ctx, result_str, (size_t)result_len, "<svgToPng>");
        free((void*)(uintptr_t)result_ptr);
        if (JS_IsException(parsed)) return JS_EXCEPTION;
        JSValue err = JS_GetPropertyStr(ctx, parsed, "__repoqlSvgError");
        if (!JS_IsException(err) && !JS_IsUndefined(err) && !JS_IsNull(err)) {
            const char* msg = JS_ToCString(ctx, err);
            JS_FreeValue(ctx, err);
            JS_FreeValue(ctx, parsed);
            if (msg == NULL) return JS_EXCEPTION;
            JSValue exc = JS_ThrowInternalError(ctx, "%s", msg);
            JS_FreeCString(ctx, msg);
            return exc;
        }
        JS_FreeValue(ctx, err);
        JS_FreeValue(ctx, parsed);
    }

    JSValue result = JS_NewStringLen(ctx, result_str, (size_t)result_len);
    free((void*)(uintptr_t)result_ptr);
    return result;
}

static int install_repoql(JSContext* ctx) {
    JSValue global_object = JS_GetGlobalObject(ctx);
    JSValue repoql_object = JS_NewObject(ctx);

    if (JS_IsException(global_object) || JS_IsException(repoql_object)) {
        JS_FreeValue(ctx, global_object);
        JS_FreeValue(ctx, repoql_object);
        return -1;
    }

    if (JS_SetPropertyStr(ctx, repoql_object, "query", JS_NewCFunction(ctx, js_repoql_query, "query", 1)) < 0 ||
        JS_SetPropertyStr(ctx, repoql_object, "read", JS_NewCFunction(ctx, js_repoql_read, "read", 2)) < 0 ||
        JS_SetPropertyStr(ctx, repoql_object, "write", JS_NewCFunction(ctx, js_repoql_write, "write", 2)) < 0 ||
        JS_SetPropertyStr(ctx, repoql_object, "delete", JS_NewCFunction(ctx, js_repoql_delete, "delete", 1)) < 0 ||
        JS_SetPropertyStr(ctx, repoql_object, "ffmpeg", JS_NewCFunction(ctx, js_repoql_ffmpeg, "ffmpeg", 1)) < 0 ||
        JS_SetPropertyStr(ctx, repoql_object, "graphviz", JS_NewCFunction(ctx, js_repoql_graphviz, "graphviz", 3)) < 0 ||
        JS_SetPropertyStr(ctx, repoql_object, "pandoc", JS_NewCFunction(ctx, js_repoql_pandoc, "pandoc", 1)) < 0 ||
        JS_SetPropertyStr(ctx, repoql_object, "svgToPng", JS_NewCFunction(ctx, js_repoql_svg_to_png, "svgToPng", 2)) < 0 ||
        JS_SetPropertyStr(ctx, global_object, "repoql", repoql_object) < 0) {
        JS_FreeValue(ctx, global_object);
        return -1;
    }

    JS_FreeValue(ctx, global_object);
    return 0;
}

static char* js_module_normalize(JSContext* ctx, const char* base_name, const char* name, void* opaque) {
    (void)base_name;
    (void)opaque;

    size_t name_len = strlen(name);
    char* normalized = js_malloc(ctx, name_len + 1);
    if (normalized == NULL) {
        JS_ThrowOutOfMemory(ctx);
        return NULL;
    }

    memcpy(normalized, name, name_len + 1);
    return normalized;
}

static JSModuleDef* js_module_loader(JSContext* ctx, const char* module_name, void* opaque) {
    (void)opaque;

    size_t module_name_len = strlen(module_name);
    int64_t packed = repoql_load_module(module_name, (int32_t)module_name_len);
    int32_t source_ptr = (int32_t)((uint64_t)packed >> 32);
    int32_t source_len = (int32_t)(packed & 0xFFFFFFFF);

    if (source_ptr == 0 || source_len <= 0) {
        JS_ThrowReferenceError(ctx, "Module not found: %s", module_name);
        return NULL;
    }

    const char* source = (const char*)(uintptr_t)source_ptr;
    JSValue func = JS_Eval(ctx, source, (size_t)source_len, module_name,
                           JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    free((void*)(uintptr_t)source_ptr);

    if (JS_IsException(func)) {
        return NULL;
    }

    JSModuleDef* module = (JSModuleDef*)JS_VALUE_GET_PTR(func);
    JS_FreeValue(ctx, func);
    return module;
}

static int64_t stringify_value(JSContext* ctx, JSValueConst value) {
    JSValue json = JS_JSONStringify(ctx, value, JS_UNDEFINED, JS_UNDEFINED);
    if (JS_IsException(json)) {
        return 0;
    }

    size_t json_len = 0;
    const char* json_text = JS_ToCStringLen(ctx, &json_len, json);
    if (json_text == NULL) {
        JS_FreeValue(ctx, json);
        return 0;
    }

    int64_t packed = pack_bytes(json_text, json_len);
    JS_FreeCString(ctx, json_text);
    JS_FreeValue(ctx, json);
    return packed;
}

static int64_t build_error_result(JSContext* ctx, const char* kind, const char* message, const char* stack) {
    JSValue root = JS_UNDEFINED;
    JSValue error = JS_UNDEFINED;
    int64_t packed = 0;

    root = JS_NewObject(ctx);
    error = JS_NewObject(ctx);
    if (JS_IsException(root) || JS_IsException(error)) {
        goto cleanup;
    }

    if (JS_SetPropertyStr(ctx, error, "kind", JS_NewString(ctx, kind)) < 0 ||
        JS_SetPropertyStr(ctx, error, "message", JS_NewString(ctx, message)) < 0 ||
        JS_SetPropertyStr(ctx, root, "error", error) < 0) {
        error = JS_UNDEFINED;
        goto cleanup;
    }

    if (stack != NULL) {
        /* error object already transferred to root, re-fetch it */
        JSValue err_ref = JS_GetPropertyStr(ctx, root, "error");
        if (!JS_IsException(err_ref)) {
            JS_SetPropertyStr(ctx, err_ref, "stack", JS_NewString(ctx, stack));
            JS_FreeValue(ctx, err_ref);
        }
    }

    error = JS_UNDEFINED;
    packed = stringify_value(ctx, root);

cleanup:
    JS_FreeValue(ctx, error);
    JS_FreeValue(ctx, root);
    return packed;
}

static int64_t exception_to_result(JSContext* ctx) {
    JSValue exception = JS_GetException(ctx);
    JSValue name_value = JS_UNDEFINED;
    JSValue message_value = JS_UNDEFINED;
    JSValue message_string = JS_UNDEFINED;
    JSValue stack_value = JS_UNDEFINED;
    int64_t packed = 0;

    const char* kind = "runtime";
    const char* message = "Unknown JavaScript error";
    const char* name_text = NULL;
    const char* message_text = NULL;
    const char* stack_text = NULL;

    name_value = JS_GetPropertyStr(ctx, exception, "name");
    if (!JS_IsException(name_value)) {
        name_text = JS_ToCString(ctx, name_value);
        if (name_text != NULL && strcmp(name_text, "SyntaxError") == 0) {
            kind = "syntax";
        }
    }

    message_value = JS_GetPropertyStr(ctx, exception, "message");
    if (JS_IsException(message_value) || JS_IsUndefined(message_value)) {
        JS_FreeValue(ctx, message_value);
        message_value = JS_UNDEFINED;
        message_string = JS_ToString(ctx, exception);
    } else {
        message_string = JS_ToString(ctx, message_value);
    }

    if (!JS_IsException(message_string)) {
        message_text = JS_ToCString(ctx, message_string);
        if (message_text != NULL) {
            message = message_text;
        }
    }

    /* Extract stack trace for source location */
    stack_value = JS_GetPropertyStr(ctx, exception, "stack");
    if (!JS_IsException(stack_value) && !JS_IsUndefined(stack_value)) {
        stack_text = JS_ToCString(ctx, stack_value);
    }

    packed = build_error_result(ctx, kind, message, stack_text);

    JS_FreeCString(ctx, stack_text);
    JS_FreeCString(ctx, message_text);
    JS_FreeCString(ctx, name_text);
    JS_FreeValue(ctx, stack_value);
    JS_FreeValue(ctx, message_string);
    JS_FreeValue(ctx, message_value);
    JS_FreeValue(ctx, name_value);
    JS_FreeValue(ctx, exception);
    return packed;
}

__attribute__((export_name("wasm_alloc")))
void* wasm_alloc(int32_t size) {
    if (size <= 0) {
        return NULL;
    }

    return malloc((size_t)size);
}

__attribute__((export_name("wasm_dealloc")))
void wasm_dealloc(void* ptr, int32_t size) {
    (void)size;
    free(ptr);
}

__attribute__((export_name("evaluate")))
int64_t evaluate(char* src_ptr, int32_t src_len, char* input_ptr, int32_t input_len) {
    JSRuntime* rt = NULL;
    JSContext* ctx = NULL;
    JSValue result = JS_UNDEFINED;
    int64_t packed = 0;

    if (src_ptr == NULL || src_len < 0 || input_len < 0) {
        return pack_cstring(kInvalidArgumentsJson);
    }

    rt = JS_NewRuntime();
    if (rt == NULL) {
        return pack_cstring(kInitFailureJson);
    }

    JS_SetMemoryLimit(rt, 128 * 1024 * 1024);
    JS_SetMaxStackSize(rt, 256 * 1024);
    JS_SetModuleLoaderFunc(rt, js_module_normalize, js_module_loader, NULL);

    ctx = JS_NewContext(rt);
    if (ctx == NULL) {
        packed = pack_cstring(kInitFailureJson);
        goto cleanup;
    }

    if (install_console(ctx) < 0) {
        packed = build_error_result(ctx, "runtime", "Failed to initialize console", NULL);
        goto cleanup;
    }

    if (install_repoql(ctx) < 0) {
        packed = build_error_result(ctx, "runtime", "Failed to initialize repoql capabilities", NULL);
        goto cleanup;
    }

    if (input_ptr != NULL && input_len > 0) {
        JSValue global_object = JS_GetGlobalObject(ctx);
        JSValue input_value = JS_ParseJSON(ctx, input_ptr, (size_t)input_len, "<input>");

        if (JS_IsException(global_object) || JS_IsException(input_value)) {
            JS_FreeValue(ctx, global_object);
            JS_FreeValue(ctx, input_value);
            packed = exception_to_result(ctx);
            goto cleanup;
        }

        if (JS_SetPropertyStr(ctx, global_object, "input", input_value) < 0) {
            JS_FreeValue(ctx, global_object);
            packed = exception_to_result(ctx);
            goto cleanup;
        }

        JS_FreeValue(ctx, global_object);
    }

    result = JS_Eval(ctx, src_ptr, (size_t)src_len, "<eval>", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(result)) {
        JS_FreeValue(ctx, result);
        result = JS_UNDEFINED;
        packed = exception_to_result(ctx);
        goto cleanup;
    }

    /* Drain the microtask queue — resolves Promises from dynamic import() */
    {
        JSContext* job_ctx = NULL;
        while (JS_ExecutePendingJob(rt, &job_ctx) > 0) {
            /* keep pumping */
        }
    }

    /* If the result is a fulfilled Promise, unwrap to its resolved value */
    if (JS_IsPromise(result)) {
        JSPromiseStateEnum state = JS_PromiseState(ctx, result);
        if (state == JS_PROMISE_FULFILLED) {
            JSValue resolved = JS_PromiseResult(ctx, result);
            JS_FreeValue(ctx, result);
            result = resolved;
        } else if (state == JS_PROMISE_REJECTED) {
            JSValue reason = JS_PromiseResult(ctx, result);
            JS_FreeValue(ctx, result);
            result = JS_UNDEFINED;
            /* Convert the rejection reason to an error result */
            const char* msg = "Promise rejected";
            const char* reason_str = JS_ToCString(ctx, reason);
            if (reason_str != NULL) {
                msg = reason_str;
            }
            packed = build_error_result(ctx, "runtime", msg, NULL);
            JS_FreeCString(ctx, reason_str);
            JS_FreeValue(ctx, reason);
            goto cleanup;
        }
        /* JS_PROMISE_PENDING: treat as undefined (shouldn't happen after draining) */
    }

    if (JS_IsUndefined(result)) {
        JS_FreeValue(ctx, result);
        result = JS_NULL;
    }

    packed = stringify_value(ctx, result);
    if (packed == 0) {
        packed = pack_cstring(kSerializationFailureJson);
    }

cleanup:
    if (ctx != NULL) {
        JS_FreeValue(ctx, result);
        JS_FreeContext(ctx);
    }

    if (rt != NULL) {
        JS_FreeRuntime(rt);
    }

    if (packed == 0) {
        packed = pack_cstring(kAllocationFailureJson);
    }

    return packed;
}
