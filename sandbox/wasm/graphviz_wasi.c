#include <gvc.h>
#include <cgraph.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

/* Static plugin registration — no dynamic loading in WASI */
extern gvplugin_library_t gvplugin_core_LTX_library;
extern gvplugin_library_t gvplugin_dot_layout_LTX_library;

lt_symlist_t lt_preloaded_symbols[] = {
    { "gvplugin_core_LTX_library",      &gvplugin_core_LTX_library },
    { "gvplugin_dot_layout_LTX_library", &gvplugin_dot_layout_LTX_library },
    { 0, 0 }
};

/*
 * Render DOT source to SVG (or other format).
 * Returns packed i64: (result_ptr << 32) | result_len
 * Caller frees via graphviz_free().
 */
__attribute__((export_name("graphviz_render")))
int64_t graphviz_render(const char* dot_source, int32_t dot_len,
                        const char* engine, int32_t engine_len,
                        const char* format, int32_t format_len) {
    if (dot_source == NULL || dot_len <= 0)
        return 0;

    /* Null-terminate strings if needed (they should be, but be safe) */
    char engine_buf[32] = "dot";
    char format_buf[32] = "svg";

    if (engine != NULL && engine_len > 0 && engine_len < 31) {
        memcpy(engine_buf, engine, engine_len);
        engine_buf[engine_len] = '\0';
    }
    if (format != NULL && format_len > 0 && format_len < 31) {
        memcpy(format_buf, format, format_len);
        format_buf[format_len] = '\0';
    }

    GVC_t *gvc = gvContextPlugins(lt_preloaded_symbols, 0);
    if (gvc == NULL)
        return 0;

    Agraph_t *graph = agmemread(dot_source);
    if (graph == NULL) {
        gvFreeContext(gvc);
        return 0;
    }

    gvLayout(gvc, graph, engine_buf);

    char *result = NULL;
    unsigned int length = 0;
    gvRenderData(gvc, graph, format_buf, &result, &length);

    gvFreeLayout(gvc, graph);
    agclose(graph);
    gvFinalize(gvc);
    gvFreeContext(gvc);

    if (result == NULL || length == 0)
        return 0;

    return ((int64_t)(uint32_t)(uintptr_t)result << 32) | (uint32_t)length;
}

__attribute__((export_name("graphviz_free")))
void graphviz_free(void* ptr) {
    gvFreeRenderData(ptr);
}
