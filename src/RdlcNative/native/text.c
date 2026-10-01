#include <pango/pangocairo.h>
#include <hb.h>
#include <stdint.h>
#include <stdlib.h>
#include <math.h>

extern hb_font_t *rdlc_font_hb_font(void *font);
extern void rdlc_font_release_hb_font(void *font);

static int utf16_position(const char *text, int bytes)
{
    int result = 0;
    const char *end = text + bytes;
    for (const char *p = text; p < end; p = g_utf8_next_char(p))
        result += g_utf8_get_char(p) > 0xffff ? 2 : 1;
    return result;
}

int rdlc_itemize(const uint16_t *text, int length, int rtl, int capacity,
                 int *starts, int *levels)
{
    glong bytes = 0;
    gchar *utf8 = g_utf16_to_utf8(text, length, NULL, &bytes, NULL);
    if (!utf8) return -1;
    PangoFontMap *map = pango_cairo_font_map_new();
    PangoContext *context = pango_font_map_create_context(map);
    GList *items = pango_itemize_with_base_dir(context,
        rtl ? PANGO_DIRECTION_RTL : PANGO_DIRECTION_LTR,
        utf8, 0, (int)bytes, NULL, NULL);
    int count = (int)g_list_length(items);
    if (count + 1 <= capacity) {
        int i = 0;
        for (GList *p = items; p; p = p->next) {
            PangoItem *item = p->data;
            starts[i] = utf16_position(utf8, item->offset);
            levels[i++] = item->analysis.level;
        }
        starts[count] = length;
        levels[count] = rtl ? 1 : 0;
    } else {
        count = -2;
    }
    g_list_free_full(items, (GDestroyNotify)pango_item_free);
    g_object_unref(context);
    g_object_unref(map);
    g_free(utf8);
    return count;
}

int rdlc_break(const uint16_t *text, int length, int rtl, uint8_t *result)
{
    glong bytes = 0;
    gchar *utf8 = g_utf16_to_utf8(text, length, NULL, &bytes, NULL);
    if (!utf8) return -1;
    int count = (int)g_utf8_strlen(utf8, bytes);
    PangoLogAttr *attrs = g_new0(PangoLogAttr, count + 1);
    pango_get_log_attrs(utf8, (int)bytes, rtl, pango_language_get_default(),
                       attrs, count + 1);
    int pos = 0, i = 0;
    for (const char *p = utf8; p < utf8 + bytes; p = g_utf8_next_char(p), i++) {
        result[pos] = (attrs[i].is_line_break ? 1 : 0)
                    | (attrs[i].is_white ? 2 : 0)
                    | (attrs[i].is_cursor_position ? 4 : 0)
                    | ((attrs[i].is_word_start || attrs[i].is_word_end) ? 8 : 0);
        if (g_utf8_get_char(p) > 0xffff)
            result[++pos] = 0;
        pos++;
    }
    g_free(attrs);
    g_free(utf8);
    return 0;
}

int rdlc_shape(void *font, const uint16_t *text, int length, int rtl,
               int capacity, uint16_t *glyphs, uint32_t *clusters,
               int *advances, int *dx, int *dy, int *abc)
{
    hb_font_t *hbfont = rdlc_font_hb_font(font);
    if (!hbfont) return -1;
    hb_buffer_t *buffer = hb_buffer_create();
    hb_buffer_add_utf16(buffer, text, length, 0, length);
    hb_buffer_set_direction(buffer, rtl ? HB_DIRECTION_RTL : HB_DIRECTION_LTR);
    hb_buffer_set_cluster_level(buffer, HB_BUFFER_CLUSTER_LEVEL_MONOTONE_CHARACTERS);
    hb_buffer_guess_segment_properties(buffer);
    hb_shape(hbfont, buffer, NULL, 0);
    unsigned count = 0;
    hb_glyph_info_t *info = hb_buffer_get_glyph_infos(buffer, &count);
    hb_glyph_position_t *positions = hb_buffer_get_glyph_positions(buffer, NULL);
    if (count > (unsigned)capacity) {
        hb_buffer_destroy(buffer);
        rdlc_font_release_hb_font(hbfont);
        return -2;
    }
    int pen = 0;
    double left = 0, right = 0;
    int has_ink = 0;
    for (unsigned i = 0; i < count; i++) {
        if (info[i].codepoint > UINT16_MAX) {
            hb_buffer_destroy(buffer);
            rdlc_font_release_hb_font(hbfont);
            return -3;
        }
        glyphs[i] = (uint16_t)info[i].codepoint;
        clusters[i] = info[i].cluster;
        advances[i] = (int)lround(positions[i].x_advance / 64.0);
        dx[i] = (int)lround(positions[i].x_offset / 64.0);
        dy[i] = (int)lround(positions[i].y_offset / 64.0);
        hb_glyph_extents_t extents;
        if (hb_font_get_glyph_extents(hbfont, info[i].codepoint, &extents) &&
            extents.width != 0) {
            double x1 = pen + (positions[i].x_offset + extents.x_bearing) / 64.0;
            double x2 = x1 + extents.width / 64.0;
            if (!has_ink || fmin(x1, x2) < left) left = fmin(x1, x2);
            if (!has_ink || fmax(x1, x2) > right) right = fmax(x1, x2);
            has_ink = 1;
        }
        pen += advances[i];
    }
    abc[0] = has_ink ? (int)floor(left) : 0;
    abc[1] = has_ink ? (int)ceil(right) - abc[0] : 0;
    abc[2] = pen - abc[0] - abc[1];
    hb_buffer_destroy(buffer);
    rdlc_font_release_hb_font(hbfont);
    return (int)count;
}

uint32_t rdlc_nominal_glyph(void *font, uint32_t codepoint)
{
    hb_font_t *hbfont = rdlc_font_hb_font(font);
    hb_codepoint_t glyph = 0;
    if (hbfont) {
        hb_font_get_nominal_glyph(hbfont, codepoint, &glyph);
        rdlc_font_release_hb_font(hbfont);
    }
    return glyph;
}

int rdlc_glyph_advance(void *font, uint32_t glyph)
{
    hb_font_t *hbfont = rdlc_font_hb_font(font);
    if (!hbfont) return 0;
    int advance = (int)lround(hb_font_get_glyph_h_advance(hbfont, glyph) / 64.0);
    rdlc_font_release_hb_font(hbfont);
    return advance;
}
