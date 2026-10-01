#include "rdlc_font.h"
#include <hb.h>
#include <hb-ot.h>
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

static void ok(int status)
{
    if (status) fprintf(stderr, "%s\n", rdlc_font_last_error());
    assert(status == 0);
}

static uint32_t be32(const unsigned char *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] << 8) | p[3];
}

int main(void)
{
    uintptr_t token, em_token, previous, selected;
    ok(rdlc_font_create("sans-serif", 0, 0, 1, 16, 96, &token));
    rdlc_font_metrics metrics = { .size = sizeof(metrics) };
    ok(rdlc_font_get_metrics(token, &metrics));
    assert(metrics.logical_em == 16 && metrics.units_per_em > 0);
    assert(metrics.cell_ascent > 0 && metrics.cell_descent >= 0);
    assert(rdlc_font_is_token(token) == 1 && rdlc_font_is_token(1) == 0);
    ok(rdlc_font_set_decorations(token, 0, 1));
    char description[4096];
    ok(rdlc_font_describe(token, description, sizeof(description)));
    puts(description);
    ok(rdlc_font_select(123, token, &previous));
    assert(previous == 0);
    ok(rdlc_font_selected(123, &selected));
    assert(selected == token);
    ok(rdlc_font_resize(token, metrics.units_per_em, &em_token));
    rdlc_font_metrics resized = { .size = sizeof(resized) };
    ok(rdlc_font_get_metrics(em_token, &resized));
    assert(resized.decorations == 2);
    ok(rdlc_font_select(123, em_token, &previous));
    assert(previous == token);

    hb_font_t *font = NULL;
    ok(rdlc_font_acquire_hb_font(token, (void **)&font));
    hb_font_t *alias = rdlc_font_hb_font((void *)token);
    assert(alias != NULL);
    rdlc_font_release_hb_font(alias);
    assert(rdlc_font_hb_font((void *)(uintptr_t)1234) == NULL);
    int x, y;
    hb_font_get_scale(font, &x, &y);
    assert(x == 1024 && y == 1024);
    assert(hb_font_is_immutable(font));
    uint32_t scalars[] = { 'A', 0xe9, 0x3a9 };
    uint16_t glyphs[4] = { 0 };
    for (size_t i = 0; i < 3; i++) {
        uint32_t gid;
        ok(rdlc_font_get_glyph(token, scalars[i], &gid));
        assert(gid && gid < 65535);
        glyphs[i + 1] = (uint16_t)gid;
        rdlc_glyph_metrics gm;
        ok(rdlc_font_get_glyph_metrics(token, gid, &gm));
        assert(fabs(gm.advance - (gm.bearing + gm.black_width + gm.trailing)) < 1e-9);
        assert(fabs(gm.advance - hb_font_get_glyph_h_advance(font, gid) / 64.0) < 0.016);
    }
    uint32_t missing;
    assert(rdlc_font_get_glyph(token, 0xd800, &missing) != 0);
    ok(rdlc_font_get_glyph(token, 0x10ffff, &missing));
    assert(missing == 0);
    float abc[3];
    ok(rdlc_font_get_winansi_widths(token, 128, 128, abc));
    uint32_t euro;
    ok(rdlc_font_get_glyph(token, 0x20ac, &euro));
    rdlc_glyph_metrics euro_metrics;
    ok(rdlc_font_get_glyph_metrics(token, euro, &euro_metrics));
    assert(fabs((abc[0] + abc[1] + abc[2]) - euro_metrics.advance) < 0.001);

    int allowed;
    ok(rdlc_font_can_embed(token, &allowed));
    assert(allowed);
    void *bytes;
    uint32_t length;
    ok(rdlc_font_package(token, glyphs, 4, &bytes, &length));
    assert(length > 12 && memcmp(bytes, "\0\1\0\0", 4) == 0);
    const unsigned char *data = bytes;
    uint32_t checksum = 0;
    for (uint32_t offset = 0; offset < length; offset += 4) {
        unsigned char padded[4] = { 0 };
        size_t n = length - offset < 4 ? length - offset : 4;
        memcpy(padded, data + offset, n);
        checksum += be32(padded);
    }
    assert(checksum == 0xb1b0afba);
    hb_blob_t *blob = hb_blob_create(bytes, length, HB_MEMORY_MODE_READONLY, NULL, NULL);
    hb_face_t *subset_face = hb_face_create(blob, 0);
    hb_font_t *subset_font = hb_font_create(subset_face);
    hb_ot_font_set_funcs(subset_font);
    hb_font_set_scale(subset_font, 1024, 1024);
    for (size_t i = 0; i < 3; i++) {
        hb_codepoint_t gid;
        assert(hb_font_get_nominal_glyph(subset_font, scalars[i], &gid));
        assert(gid == glyphs[i + 1]);
        assert(hb_font_get_glyph_h_advance(font, gid) ==
               hb_font_get_glyph_h_advance(subset_font, gid));
    }
    hb_font_destroy(subset_font);
    hb_face_destroy(subset_face);
    hb_blob_destroy(blob);
    rdlc_font_free_buffer(bytes);
    ok(rdlc_font_destroy(token));
    /* An acquired HB reference must not borrow the registry token's lifetime. */
    assert(hb_font_get_glyph_h_advance(font, glyphs[1]) > 0);
    rdlc_font_release_hb_font(font);
    assert(rdlc_font_get_metrics(token, &metrics) != 0);
    ok(rdlc_font_destroy(em_token));
    assert(rdlc_font_selected(123, &selected) != 0);
    ok(rdlc_font_forget_hdc(123));
    assert(rdlc_font_destroy(1234) != 0);
    assert(rdlc_font_create("rdlc-definitely-nonexistent-family", 0, 0, 1, 16, 96, &token) != 0);
    assert(rdlc_font_create_file("/dev/null", 0, 0, 0, 1, 16, 96, &token) != 0);
    assert(rdlc_font_create("sans-serif", 0, 0, 2, 16, 96, &token) != 0);
    ok(rdlc_font_create("sans-serif", 0, 1, 1, 16, 96, &token));
    metrics.size = sizeof(metrics);
    ok(rdlc_font_get_metrics(token, &metrics));
    assert(metrics.italic == 1 && metrics.italic_angle < 0);
    ok(rdlc_font_destroy(token));
    ok(rdlc_font_create("sans-serif", 1, 0, 1, 16, 96, &token));
    ok(rdlc_font_get_metrics(token, &metrics));
    assert(metrics.weight >= 600);
    ok(rdlc_font_destroy(token));
    puts("Native font contract assertions passed");
    return 0;
}
