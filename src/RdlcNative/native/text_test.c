#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <uchar.h>

extern int rdlc_itemize(const uint16_t *, int, int, int, int *, int *);
extern int rdlc_break(const uint16_t *, int, int, uint8_t *);

int main(void)
{
    const char16_t text[] = u"فاتورة INV-123: المبلغ 123.45 EUR";
    const int length = (int)(sizeof(text) / sizeof(text[0])) - 1;
    int starts[64], levels[64];
    int count = rdlc_itemize(text, length, 1, 64, starts, levels);
    assert(count > 0 && starts[0] == 0 && starts[count] == length);
    int amount = -1;
    for (int i = 0; i + 5 < length; i++)
        if (text[i] == '1' && text[i + 3] == '.') amount = i;
    assert(amount >= 0);
    int found = 0;
    for (int i = 0; i < count; i++) {
        printf("item %d: UTF16[%d,%d), bidi level %d\n",
               i, starts[i], starts[i + 1], levels[i]);
        if (amount >= starts[i] && amount < starts[i + 1]) {
            assert((levels[i] & 1) == 0);
            found = 1;
        }
    }
    assert(found);
    uint8_t attrs[64] = {0};
    assert(rdlc_break(text, length, 1, attrs) == 0);
    puts("Pango keeps the decimal amount in an LTR run inside an RTL paragraph.");
    return 0;
}
