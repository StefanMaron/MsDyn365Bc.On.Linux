#ifndef RDLC_FONT_H
#define RDLC_FONT_H

#include <stddef.h>
#include <stdint.h>

typedef struct hb_font_t hb_font_t;

#ifdef __cplusplus
extern "C" {
#endif

/* Status-returning functions return zero on success.
 * Tokens are registry IDs, never GDI+, FreeType, or HarfBuzz pointers.
 * HarfBuzz positions use 64 units per logical pixel, including EM-sized fonts.
 */
typedef struct {
    uint32_t size, units_per_em, glyph_count, weight;
    uint32_t fs_type, os2_version, italic, charset;
    uint32_t first_char, last_char, default_char, break_char;
    uint32_t pitch_flags, decorations, reserved1, reserved2;
    double logical_em, dpi;
    double cell_ascent, cell_descent, internal_leading, external_leading;
    double typo_ascent, typo_descent, line_gap;
    double x_min, y_min, x_max, y_max, italic_angle;
    double average_width, maximum_width;
    double underline_position, underline_thickness;
    double strikeout_position, strikeout_thickness;
} rdlc_font_metrics;

typedef struct {
    double bearing, black_width, trailing, advance;
} rdlc_glyph_metrics;

const char *rdlc_font_last_error(void);
int rdlc_font_create(const char *family, int bold, int italic, int charset,
                     double logical_em, double dpi, uintptr_t *token);
int rdlc_font_create_file(const char *path, unsigned face_index, int bold,
                          int italic, int charset, double logical_em,
                          double dpi, uintptr_t *token);
int rdlc_font_resize(uintptr_t source, double logical_em, uintptr_t *token);
int rdlc_font_set_decorations(uintptr_t token, int underline, int strikeout);
int rdlc_font_destroy(uintptr_t token);
/* Boolean ownership query: 1 for a live token, 0 otherwise. */
int rdlc_font_is_token(uintptr_t token);
int rdlc_font_select(uintptr_t hdc, uintptr_t token, uintptr_t *previous);
int rdlc_font_selected(uintptr_t hdc, uintptr_t *token);
int rdlc_font_forget_hdc(uintptr_t hdc);
int rdlc_font_get_metrics(uintptr_t token, rdlc_font_metrics *metrics);
int rdlc_font_get_glyph(uintptr_t token, uint32_t unicode, uint32_t *glyph);
int rdlc_font_get_glyph_metrics(uintptr_t token, uint32_t glyph,
                                rdlc_glyph_metrics *metrics);
int rdlc_font_get_winansi_widths(uintptr_t token, uint32_t first,
                                 uint32_t last, float *abc);
int rdlc_font_can_embed(uintptr_t token, int *allowed);
int rdlc_font_package(uintptr_t token, const uint16_t *glyphs, uint32_t count,
                       void **bytes, uint32_t *length);
void rdlc_font_free_buffer(void *bytes);
int rdlc_font_describe(uintptr_t token, char *buffer, size_t capacity);

/* Acquire returns an owned reference to an immutable hb_font_t. Do not change
 * its scale or funcs. Release even if the originating registry token is closed.
 * hb_font_get_face() is borrowed from this reference and has the same lifetime.
 */
int rdlc_font_acquire_hb_font(uintptr_t token, void **hb_font);
/* Convenience API for the text bridge: token is opaque, not dereferenced.
 * Returns an OWNED reference, or NULL with rdlc_font_last_error() set.
 */
hb_font_t *rdlc_font_hb_font(void *provider_font);
void rdlc_font_release_hb_font(void *hb_font);

#ifdef __cplusplus
}
#endif
#endif
