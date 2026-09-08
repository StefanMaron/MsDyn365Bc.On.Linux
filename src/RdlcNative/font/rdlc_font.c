#include "rdlc_font.h"
#include <fontconfig/fontconfig.h>
#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_TRUETYPE_TABLES_H
#include <hb.h>
#include <hb-ot.h>
#include <hb-subset.h>
#include <errno.h>
#include <limits.h>
#include <math.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct font_record {
    uintptr_t token;
    char *path;
    unsigned index;
    int bold, italic, charset;
    double em, dpi;
    FT_Face ft;
    hb_blob_t *blob;
    hb_face_t *face;
    hb_font_t *font;
    rdlc_font_metrics metrics;
    struct font_record *next;
} font_record;

typedef struct selection {
    uintptr_t hdc, token;
    struct selection *next;
} selection;

static pthread_mutex_t gate = PTHREAD_MUTEX_INITIALIZER;
static FT_Library ft_library;
static font_record *fonts;
static selection *selections;
#if UINTPTR_MAX > UINT32_MAX
static uintptr_t next_token = UINT64_C(0x52444c4300000001);
#else
static uintptr_t next_token = UINT32_C(0x52440001);
#endif
static _Thread_local char last_error[1024];

static int fail(const char *format, ...)
{
    va_list args;
    va_start(args, format);
    vsnprintf(last_error, sizeof(last_error), format, args);
    va_end(args);
    return -1;
}

const char *rdlc_font_last_error(void) { return last_error; }

static font_record *find_font(uintptr_t token)
{
    for (font_record *f = fonts; f; f = f->next)
        if (f->token == token)
            return f;
    fail("Unknown or closed provider font token %llu", (unsigned long long)token);
    return NULL;
}

static void dispose_font(font_record *f)
{
    if (!f) return;
    if (f->ft) FT_Done_Face(f->ft);
    if (f->font) hb_font_destroy(f->font);
    if (f->face) hb_face_destroy(f->face);
    if (f->blob) hb_blob_destroy(f->blob);
    free(f->path);
    free(f);
}

static int has_table(FT_Face face, FT_ULong tag)
{
    FT_ULong length = 0;
    return FT_Load_Sfnt_Table(face, tag, 0, NULL, &length) == 0 && length != 0;
}

static int permissions(font_record *f, int *allowed, int *no_subset)
{
    unsigned type = f->metrics.fs_type;
    unsigned usage = type & 15;
    unsigned version = f->metrics.os2_version;
    if ((usage & 1) || (version >= 3 &&
        usage != 0 && usage != 2 && usage != 4 && usage != 8))
        return fail("Invalid OS/2 embedding permission value 0x%x", type);
    /* Older OS/2 versions permit the least restrictive of multiple usage bits. */
    *allowed = usage == 0 || (usage & (4 | 8)) != 0;
    if (version >= 2 && (type & 0x200))
        *allowed = 0;
    *no_subset = version >= 2 && (type & 0x100) != 0;
    return 0;
}

static int read_metrics(font_record *f)
{
    TT_OS2 *os2 = FT_Get_Sfnt_Table(f->ft, ft_sfnt_os2);
    TT_HoriHeader *hhea = FT_Get_Sfnt_Table(f->ft, ft_sfnt_hhea);
    TT_Postscript *post = FT_Get_Sfnt_Table(f->ft, ft_sfnt_post);
    if (!os2 || os2->version == 0xffff || !hhea || !post)
        return fail("TrueType face needs OS/2, hhea and post tables: %s", f->path);
    if (os2->version > 5)
        return fail("Unsupported OS/2 version %u", os2->version);
    double upem = f->ft->units_per_EM;
    double scale = f->em / upem;
    double ascent = os2->usWinAscent, descent = os2->usWinDescent;
    double spacing = fmax(ascent + descent,
                         hhea->Ascender - hhea->Descender + hhea->Line_Gap);
    if (os2->version >= 4 && (os2->fsSelection & 0x80)) {
        ascent = os2->sTypoAscender;
        descent = -os2->sTypoDescender;
        spacing = ascent + descent + os2->sTypoLineGap;
    }
    if (ascent <= 0 || descent < 0 || spacing < ascent + descent)
        return fail("Unsupported inconsistent vertical font metrics: %s", f->path);
    rdlc_font_metrics *m = &f->metrics;
    m->size = sizeof(*m);
    m->units_per_em = f->ft->units_per_EM;
    m->glyph_count = (uint32_t)f->ft->num_glyphs;
    m->weight = os2->usWeightClass;
    m->fs_type = os2->fsType;
    m->os2_version = os2->version;
    m->italic = f->italic;
    m->charset = f->charset;
    FT_UInt gid;
    FT_ULong codepoint = FT_Get_First_Char(f->ft, &gid);
    int first = 1;
    while (gid && codepoint <= 65535) {
        if (first) {
            m->first_char = (uint32_t)codepoint;
            first = 0;
        }
        m->last_char = (uint32_t)codepoint;
        codepoint = FT_Get_Next_Char(f->ft, codepoint, &gid);
    }
    m->default_char = os2->version >= 2 ? os2->usDefaultChar : 0;
    m->break_char = os2->version >= 2 ? os2->usBreakChar : 32;
    /* TMPF_FIXED_PITCH is counterintuitively set for variable-pitch fonts. */
    m->pitch_flags = FT_IS_FIXED_WIDTH(f->ft) ? 4 : 5;
    m->logical_em = f->em;
    m->dpi = f->dpi;
    m->cell_ascent = ascent * scale;
    m->cell_descent = descent * scale;
    m->internal_leading = fmax(0, ascent + descent - upem) * scale;
    m->external_leading = (spacing - ascent - descent) * scale;
    m->typo_ascent = os2->sTypoAscender * scale;
    m->typo_descent = os2->sTypoDescender * scale;
    m->line_gap = os2->sTypoLineGap * scale;
    m->x_min = f->ft->bbox.xMin * scale;
    m->y_min = f->ft->bbox.yMin * scale;
    m->x_max = f->ft->bbox.xMax * scale;
    m->y_max = f->ft->bbox.yMax * scale;
    m->italic_angle = post->italicAngle / 65536.0;
    m->average_width = os2->xAvgCharWidth * scale;
    m->maximum_width = hhea->advance_Width_Max * scale;
    m->underline_position = post->underlinePosition * scale;
    m->underline_thickness = post->underlineThickness * scale;
    m->strikeout_position = os2->yStrikeoutPosition * scale;
    m->strikeout_thickness = os2->yStrikeoutSize * scale;
    int allowed, no_subset;
    return permissions(f, &allowed, &no_subset);
}

/* Caller holds gate. Font bytes are immutable and retained by the HB blob. */
static int create_file(const char *path, unsigned index, int bold, int italic,
                       int charset, double em, double dpi, uintptr_t *token,
                       hb_blob_t *pinned_blob)
{
    if (!path || !*path || !token || index > 65535 ||
        !isfinite(em) || em <= 0 || em > INT_MAX / 64.0 ||
        !isfinite(dpi) || dpi <= 0 || dpi > 100000 ||
        (bold != 0 && bold != 1) || (italic != 0 && italic != 1) ||
        charset < 0 || charset > 255)
        return fail("Invalid font creation arguments");
    if (charset == 2)
        return fail("Symbol-encoded fonts are not supported by this Unicode provider");
    if (!ft_library && FT_Init_FreeType(&ft_library))
        return fail("FT_Init_FreeType failed");
    font_record *f = calloc(1, sizeof(*f));
    if (!f) return fail("Out of memory creating font");
    f->path = strdup(path);
    f->index = index;
    f->bold = bold;
    f->italic = italic;
    f->charset = charset;
    f->em = em;
    f->dpi = dpi;
    if (!f->path) {
        dispose_font(f);
        return fail("Out of memory copying font path");
    }
    if (pinned_blob) {
        f->blob = hb_blob_reference(pinned_blob);
    } else {
        hb_blob_t *file_blob = hb_blob_create_from_file_or_fail(path);
        if (file_blob) {
            unsigned file_length;
            const char *file_bytes = hb_blob_get_data(file_blob, &file_length);
            /* Snapshot rather than retain an mmap that an in-place file write
             * could change underneath glyph IDs already emitted to the PDF. */
            f->blob = hb_blob_create(file_bytes, file_length, HB_MEMORY_MODE_DUPLICATE,
                                     NULL, NULL);
            hb_blob_destroy(file_blob);
        }
    }
    if (f->blob) hb_blob_make_immutable(f->blob);
    unsigned length = 0;
    const char *bytes = f->blob ? hb_blob_get_data(f->blob, &length) : NULL;
    if (!bytes || length == 0 || length > INT_MAX ||
        FT_New_Memory_Face(ft_library, (const FT_Byte *)bytes,
                           (FT_Long)length, index, &f->ft)) {
        dispose_font(f);
        return fail("Cannot open SFNT face %u from %s", index, path);
    }
    if (!FT_IS_SFNT(f->ft) || !FT_IS_SCALABLE(f->ft) ||
        FT_HAS_MULTIPLE_MASTERS(f->ft) || FT_HAS_COLOR(f->ft) ||
        !f->ft->units_per_EM || f->ft->num_glyphs <= 0 ||
        f->ft->num_glyphs > 65535 ||
        !has_table(f->ft, FT_MAKE_TAG('g','l','y','f')) ||
        !has_table(f->ft, FT_MAKE_TAG('l','o','c','a')) ||
        has_table(f->ft, FT_MAKE_TAG('S','V','G',' '))) {
        dispose_font(f);
        return fail("Only static, monochrome glyf/loca TrueType faces are supported: %s", path);
    }
    TT_OS2 *os2 = FT_Get_Sfnt_Table(f->ft, ft_sfnt_os2);
    int actual_bold = os2 && os2->version != 0xffff ?
        os2->usWeightClass >= 600 : (f->ft->style_flags & FT_STYLE_FLAG_BOLD) != 0;
    int actual_italic = (f->ft->style_flags & FT_STYLE_FLAG_ITALIC) != 0;
    if (actual_bold != bold || actual_italic != italic) {
        dispose_font(f);
        return fail("Synthetic or mismatched bold/italic style is unsupported: %s", path);
    }
    if (FT_Select_Charmap(f->ft, FT_ENCODING_UNICODE)) {
        dispose_font(f);
        return fail("Font has no Unicode cmap: %s", path);
    }
    f->face = hb_face_create(f->blob, index);
    f->font = hb_font_create(f->face);
    if (hb_face_get_upem(f->face) != f->ft->units_per_EM ||
        hb_face_get_glyph_count(f->face) != (unsigned)f->ft->num_glyphs ||
        f->font == hb_font_get_empty()) {
        dispose_font(f);
        return fail("HarfBuzz and FreeType disagree on the selected face: %s", path);
    }
    hb_ot_font_set_funcs(f->font);
    hb_font_set_scale(f->font, (int)llround(em * 64), (int)llround(em * 64));
    hb_face_make_immutable(f->face);
    hb_font_make_immutable(f->font);
    if (read_metrics(f)) {
        dispose_font(f);
        return -1;
    }
    if (next_token == 0 || next_token == UINTPTR_MAX) {
        dispose_font(f);
        return fail("Font token space exhausted");
    }
    f->token = next_token++;
    f->next = fonts;
    fonts = f;
    *token = f->token;
    return 0;
}

int rdlc_font_create_file(const char *path, unsigned index, int bold, int italic,
                          int charset, double em, double dpi, uintptr_t *token)
{
    pthread_mutex_lock(&gate);
    int result = create_file(path, index, bold, italic, charset, em, dpi, token, NULL);
    pthread_mutex_unlock(&gate);
    return result;
}

int rdlc_font_create(const char *family, int bold, int italic, int charset,
                     double em, double dpi, uintptr_t *token)
{
    if (!family || !*family || !token) return fail("Missing font family/output");
    pthread_mutex_lock(&gate);
    FcPattern *request = FcPatternCreate();
    FcPattern *match = NULL;
    int result = -1;
    if (!request || !FcPatternAddString(request, FC_FAMILY, (const FcChar8 *)family) ||
        !FcPatternAddInteger(request, FC_WEIGHT, bold ? FC_WEIGHT_BOLD : FC_WEIGHT_REGULAR) ||
        !FcPatternAddInteger(request, FC_SLANT, italic ? FC_SLANT_ITALIC : FC_SLANT_ROMAN) ||
        !FcPatternAddBool(request, FC_SCALABLE, FcTrue) ||
        !FcConfigSubstitute(NULL, request, FcMatchPattern)) {
        fail("Cannot construct Fontconfig request");
        goto done;
    }
    FcDefaultSubstitute(request);
    FcResult fc_result;
    match = FcFontMatch(NULL, request, &fc_result);
    FcChar8 *file = NULL;
    int index = 0;
    if (!match || FcPatternGetString(match, FC_FILE, 0, &file) != FcResultMatch ||
        FcPatternGetInteger(match, FC_INDEX, 0, &index) != FcResultMatch || index < 0) {
        fail("Fontconfig did not resolve a file/index for '%s'", family);
        goto done;
    }
    /* Explicit generic families request matching; other unresolved names fail
     * rather than silently becoming an unrelated font with different glyph IDs. */
    int accepted = !strcmp(family, "sans-serif") || !strcmp(family, "serif") ||
                   !strcmp(family, "monospace");
    for (int i = 0; !accepted; i++) {
        FcChar8 *resolved = NULL;
        if (FcPatternGetString(match, FC_FAMILY, i, &resolved) != FcResultMatch) break;
        accepted = FcStrCmpIgnoreCase(resolved, (const FcChar8 *)family) == 0;
    }
    if (!accepted) {
        fail("Fontconfig substituted '%s'; bind the actual fallback face explicitly", family);
        goto done;
    }
    result = create_file((const char *)file, (unsigned)index, bold, italic,
                         charset, em, dpi, token, NULL);
done:
    if (match) FcPatternDestroy(match);
    if (request) FcPatternDestroy(request);
    pthread_mutex_unlock(&gate);
    return result;
}

int rdlc_font_resize(uintptr_t source, double em, uintptr_t *token)
{
    pthread_mutex_lock(&gate);
    font_record *f = find_font(source);
    int result = f ? create_file(f->path, f->index, f->bold, f->italic,
                                  f->charset, em, f->dpi, token, f->blob) : -1;
    if (!result) find_font(*token)->metrics.decorations = f->metrics.decorations;
    pthread_mutex_unlock(&gate);
    return result;
}

int rdlc_font_set_decorations(uintptr_t token, int underline, int strikeout)
{
    if ((underline != 0 && underline != 1) || (strikeout != 0 && strikeout != 1))
        return fail("Invalid font decoration flags");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    if (f) f->metrics.decorations = (uint32_t)(underline | (strikeout << 1));
    pthread_mutex_unlock(&gate);
    return f ? 0 : -1;
}

int rdlc_font_destroy(uintptr_t token)
{
    pthread_mutex_lock(&gate);
    font_record **link = &fonts;
    while (*link && (*link)->token != token) link = &(*link)->next;
    if (!*link) {
        pthread_mutex_unlock(&gate);
        return fail("Cannot release unknown/closed provider token");
    }

    font_record *f = *link;
    *link = f->next;
    for (selection *s = selections; s; s = s->next)
        if (s->token == token) s->token = 0;
    dispose_font(f);
    pthread_mutex_unlock(&gate);
    return 0;
}

int rdlc_font_is_token(uintptr_t token)
{
    pthread_mutex_lock(&gate);
    int found = 0;
    for (font_record *f = fonts; f; f = f->next)
        if (f->token == token) {
            found = 1;
            break;
        }
    pthread_mutex_unlock(&gate);
    return found;
}

int rdlc_font_select(uintptr_t hdc, uintptr_t token, uintptr_t *previous)
{
    if (!hdc || !previous) return fail("Invalid HDC/previous selection output");
    pthread_mutex_lock(&gate);
    if (token && !find_font(token)) {
        pthread_mutex_unlock(&gate);
        return -1;
    }
    selection *s = selections;
    while (s && s->hdc != hdc) s = s->next;
    if (!s) {
        s = calloc(1, sizeof(*s));
        if (!s) {
            pthread_mutex_unlock(&gate);
            return fail("Out of memory tracking HDC");
        }
        s->hdc = hdc;
        s->next = selections;
        selections = s;
    }
    *previous = s->token;
    s->token = token;
    pthread_mutex_unlock(&gate);
    return 0;
}

int rdlc_font_selected(uintptr_t hdc, uintptr_t *token)
{
    if (!hdc || !token) return fail("Invalid HDC/token output");
    pthread_mutex_lock(&gate);
    selection *s = selections;
    while (s && s->hdc != hdc) s = s->next;
    int result = s && s->token && find_font(s->token) ? 0 :
        fail("HDC has no selected live provider font");
    if (!result) *token = s->token;
    pthread_mutex_unlock(&gate);
    return result;
}

int rdlc_font_forget_hdc(uintptr_t hdc)
{
    pthread_mutex_lock(&gate);
    selection **link = &selections;
    while (*link && (*link)->hdc != hdc) link = &(*link)->next;
    if (*link) {
        selection *s = *link;
        *link = s->next;
        free(s);
    }
    pthread_mutex_unlock(&gate);
    return 0;
}

int rdlc_font_get_metrics(uintptr_t token, rdlc_font_metrics *metrics)
{
    if (!metrics || metrics->size != sizeof(*metrics))
        return fail("rdlc_font_metrics ABI size mismatch");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    if (f) *metrics = f->metrics;
    pthread_mutex_unlock(&gate);
    return f ? 0 : -1;
}

int rdlc_font_get_glyph(uintptr_t token, uint32_t unicode, uint32_t *glyph)
{
    if (!glyph || unicode > 0x10ffff || (unicode >= 0xd800 && unicode <= 0xdfff))
        return fail("Invalid Unicode scalar/output");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    if (f) *glyph = FT_Get_Char_Index(f->ft, unicode);
    pthread_mutex_unlock(&gate);
    return f ? 0 : -1; /* Glyph zero explicitly means missing/.notdef. */
}

static int glyph_metrics(font_record *f, uint32_t glyph, rdlc_glyph_metrics *m)
{
    if (glyph >= f->metrics.glyph_count)
        return fail("Glyph %u is outside selected face", glyph);
    FT_Error error = FT_Load_Glyph(f->ft, glyph,
                                  FT_LOAD_NO_SCALE | FT_LOAD_NO_HINTING | FT_LOAD_NO_BITMAP);
    if (error) return fail("FT_Load_Glyph(%u) failed: %d", glyph, error);
    double scale = f->em / f->metrics.units_per_em;
    FT_Glyph_Metrics *g = &f->ft->glyph->metrics;
    m->bearing = g->horiBearingX * scale;
    m->black_width = g->width * scale;
    m->advance = g->horiAdvance * scale;
    m->trailing = m->advance - m->bearing - m->black_width;
    return 0;
}

int rdlc_font_get_glyph_metrics(uintptr_t token, uint32_t glyph, rdlc_glyph_metrics *m)
{
    if (!m) return fail("Missing glyph metric output");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    int result = f ? glyph_metrics(f, glyph, m) : -1;
    pthread_mutex_unlock(&gate);
    return result;
}

int rdlc_font_get_winansi_widths(uintptr_t token, uint32_t first, uint32_t last, float *abc)
{
    static const uint16_t cp1252[32] = {
        0x20ac,0,0x201a,0x192,0x201e,0x2026,0x2020,0x2021,
        0x2c6,0x2030,0x160,0x2039,0x152,0,0x17d,0,
        0,0x2018,0x2019,0x201c,0x201d,0x2022,0x2013,0x2014,
        0x2dc,0x2122,0x161,0x203a,0x153,0,0x17e,0x178
    };
    if (!abc || first > last || last > 255) return fail("Invalid WinAnsi width range");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    int result = f ? 0 : -1;
    for (uint32_t c = first; !result && c <= last; c++) {
        uint32_t unicode = c >= 128 && c < 160 ? cp1252[c - 128] : c;
        uint32_t glyph = unicode ? FT_Get_Char_Index(f->ft, unicode) : 0;
        rdlc_glyph_metrics m;
        result = glyph_metrics(f, glyph, &m);
        if (!result) {
            size_t offset = (c - first) * 3;
            abc[offset] = (float)m.bearing;
            abc[offset + 1] = (float)m.black_width;
            abc[offset + 2] = (float)m.trailing;
        }
    }
    pthread_mutex_unlock(&gate);
    return result;
}

int rdlc_font_can_embed(uintptr_t token, int *allowed)
{
    if (!allowed) return fail("Missing embedding permission output");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    int no_subset;
    int result = f ? permissions(f, allowed, &no_subset) : -1;
    pthread_mutex_unlock(&gate);
    return result;
}

int rdlc_font_package(uintptr_t token, const uint16_t *glyphs, uint32_t count,
                       void **bytes, uint32_t *length)
{
    if (!bytes || !length || !glyphs || !count)
        return fail("Missing font package arguments/glyphs");
    *bytes = NULL;
    *length = 0;
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    hb_subset_input_t *input = NULL;
    hb_face_t *subset = NULL;
    hb_blob_t *output = NULL;
    int result = -1, allowed, no_subset;
    if (!f || permissions(f, &allowed, &no_subset)) goto done;
    if (!allowed) {
        fail("OS/2 permissions prohibit outline font embedding");
        goto done;
    }
    for (uint32_t i = 0; i < count; i++)
        if (glyphs[i] >= f->metrics.glyph_count) {
            fail("Cannot embed glyph %u from this face", glyphs[i]);
            goto done;
        }
    if (no_subset) {
        unsigned n;
        const char *source = hb_blob_get_data(f->blob, &n);
        if (n < 4 || !memcmp(source, "ttcf", 4)) {
            fail("No-subsetting TTC extraction is not implemented");
            goto done;
        }
        output = hb_blob_reference(f->blob);
    } else {
        input = hb_subset_input_create_or_fail();
        if (!input) {
            fail("Cannot allocate HarfBuzz subset input");
            goto done;
        }
        hb_set_t *keep = hb_subset_input_glyph_set(input);
        hb_set_t *unicodes = hb_subset_input_unicode_set(input);
        hb_set_clear(unicodes);
        hb_set_add(keep, 0);
        for (uint32_t i = 0; i < count; i++) hb_set_add(keep, glyphs[i]);
        /* Keep cmap entries for retained glyphs, including the simple PDF font
         * path. This is bounded by the face cmap, not a Unicode scalar scan. */
        FT_UInt gid;
        FT_ULong codepoint = FT_Get_First_Char(f->ft, &gid);
        while (gid) {
            if (hb_set_has(keep, gid)) hb_set_add(unicodes, (hb_codepoint_t)codepoint);
            codepoint = FT_Get_Next_Char(f->ft, codepoint, &gid);
        }
        if (!hb_set_allocation_successful(keep) || !hb_set_allocation_successful(unicodes)) {
            fail("Out of memory collecting subset glyphs/cmap");
            goto done;
        }
        hb_subset_input_set_flags(input, HB_SUBSET_FLAGS_RETAIN_GIDS |
                                         HB_SUBSET_FLAGS_NOTDEF_OUTLINE |
                                         HB_SUBSET_FLAGS_NAME_LEGACY);
        subset = hb_subset_or_fail(f->face, input);
        if (!subset) {
            fail("HarfBuzz failed to subset selected TrueType face");
            goto done;
        }
        output = hb_face_reference_blob(subset);
    }
    unsigned n;
    const char *data = hb_blob_get_data(output, &n);
    if (!data || n < 12 || memcmp(data, "\0\1\0\0", 4)) {
        fail("Subset output is not standalone TrueType SFNT");
        goto done;
    }
    void *copy = malloc(n);
    if (!copy) {
        fail("Out of memory copying font package");
        goto done;
    }
    memcpy(copy, data, n);
    *bytes = copy;
    *length = n;
    result = 0;
done:
    if (output) hb_blob_destroy(output);
    if (subset) hb_face_destroy(subset);
    if (input) hb_subset_input_destroy(input);
    pthread_mutex_unlock(&gate);
    return result;
}

void rdlc_font_free_buffer(void *bytes) { free(bytes); }

int rdlc_font_acquire_hb_font(uintptr_t token, void **font)
{
    if (!font) return fail("Missing HarfBuzz font output");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    if (f) *font = hb_font_reference(f->font);
    pthread_mutex_unlock(&gate);
    return f ? 0 : -1;
}

hb_font_t *rdlc_font_hb_font(void *provider_font)
{
    void *font = NULL;
    if (rdlc_font_acquire_hb_font((uintptr_t)provider_font, &font)) return NULL;
    return (hb_font_t *)font;
}

void rdlc_font_release_hb_font(void *font)
{
    if (font) hb_font_destroy((hb_font_t *)font);
}

int rdlc_font_describe(uintptr_t token, char *buffer, size_t capacity)
{
    if (!buffer || !capacity) return fail("Missing description output");
    pthread_mutex_lock(&gate);
    font_record *f = find_font(token);
    int result = -1;
    if (f) {
        int n = snprintf(buffer, capacity, "%s#index=%u;family=%s;em=%.9g;dpi=%.9g",
                         f->path, f->index, f->ft->family_name, f->em, f->dpi);
        result = n >= 0 && (size_t)n < capacity ? 0 : fail("Description buffer too small");
    }
    pthread_mutex_unlock(&gate);
    return result;
}
