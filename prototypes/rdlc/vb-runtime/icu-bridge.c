#include <pthread.h>
#include <stdint.h>

typedef struct SortHandle SortHandle;
extern int32_t GlobalizationNative_LoadICU(void);
extern int32_t GlobalizationNative_GetSortHandle(const char *, SortHandle **);
extern void GlobalizationNative_CloseSortHandle(SortHandle *);
extern int32_t GlobalizationNative_CompareString(SortHandle *, const uint16_t *, int32_t,
                                               const uint16_t *, int32_t, int32_t);
extern int32_t GlobalizationNative_LastIndexOf(SortHandle *, const uint16_t *, int32_t,
                                             const uint16_t *, int32_t, int32_t, int32_t *);

/* Mono AppDomains have separate managed statics but share this native library. */
static pthread_once_t once = PTHREAD_ONCE_INIT;
static int32_t initialized;

static void initialize(void)
{
    initialized = GlobalizationNative_LoadICU();
}

int32_t BCRdlc_LoadICU(void)
{
    int status = pthread_once(&once, initialize);
    return status == 0 ? initialized : -status;
}

int32_t BCRdlc_GetSortHandle(const char *locale, SortHandle **handle)
{
    return GlobalizationNative_GetSortHandle(locale, handle);
}

void BCRdlc_CloseSortHandle(SortHandle *handle)
{
    GlobalizationNative_CloseSortHandle(handle);
}

int32_t BCRdlc_CompareString(SortHandle *handle, const uint16_t *left, int32_t left_length,
                            const uint16_t *right, int32_t right_length, int32_t options)
{
    return GlobalizationNative_CompareString(handle, left, left_length, right, right_length, options);
}

int32_t BCRdlc_LastIndexOf(SortHandle *handle, const uint16_t *value, int32_t value_length,
                         const uint16_t *source, int32_t source_length, int32_t options, int32_t *matched_length)
{
    return GlobalizationNative_LastIndexOf(handle, value, value_length, source, source_length, options, matched_length);
}
