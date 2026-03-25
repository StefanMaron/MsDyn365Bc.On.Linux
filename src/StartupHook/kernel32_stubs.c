// Stub implementations for Windows P/Invoke functions used by BC service tier on Linux.
// Compiled to libwin32_stubs.so and loaded via NativeLibrary.ResolvingUnmanagedDll.
// Provides no-op/stub implementations for: kernel32, user32, Wintrust, nclcsrts,
// dhcpcsvc, Netapi32, ntdsapi, rpcrt4, advapi32.

#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

typedef intptr_t HANDLE;

// =============================================================================
// kernel32.dll
// =============================================================================

// --- Job Object (process groups, not needed on Linux) ---
HANDLE OpenJobObject(uint32_t a, int b, const void* c) { return 0; }
HANDLE CreateJobObject(HANDLE a, const void* b) { return (HANDLE)0xDEAD; }
int SetInformationJobObject(HANDLE a, int b, HANDLE c, uint32_t d) { return 1; }
int AssignProcessToJobObject(HANDLE a, HANDLE b) { return 1; }
int IsProcessInJob(HANDLE a, HANDLE b) { return 0; }
int CloseHandle(HANDLE h) { return 1; }

// --- Memory info ---
int GetPhysicallyInstalledSystemMemory(int64_t* totalKB) { *totalKB = 16 * 1024 * 1024; return 1; }

typedef struct {
    uint32_t dwLength;
    uint32_t dwMemoryLoad;
    uint64_t ullTotalPhys;
    uint64_t ullAvailPhys;
    uint64_t ullTotalPageFile;
    uint64_t ullAvailPageFile;
    uint64_t ullTotalVirtual;
    uint64_t ullAvailVirtual;
    uint64_t ullAvailExtendedVirtual;
} MEMORYSTATUSEX;

int GlobalMemoryStatusEx(MEMORYSTATUSEX* ms) {
    ms->dwMemoryLoad = 50;
    ms->ullTotalPhys = 16ULL * 1024 * 1024 * 1024;
    ms->ullAvailPhys = 8ULL * 1024 * 1024 * 1024;
    ms->ullTotalPageFile = 16ULL * 1024 * 1024 * 1024;
    ms->ullAvailPageFile = 8ULL * 1024 * 1024 * 1024;
    ms->ullTotalVirtual = 128ULL * 1024 * 1024 * 1024;
    ms->ullAvailVirtual = 128ULL * 1024 * 1024 * 1024;
    ms->ullAvailExtendedVirtual = 0;
    return 1;
}

// --- Module/library ---
int FreeLibrary(HANDLE h) { return 1; }
HANDLE GetModuleHandle(const void* name) { return 0; }
HANDLE GetModuleHandleW(const void* name) { return 0; }
HANDLE LoadLibraryExW(const void* lpFileName, HANDLE hFile, uint32_t dwFlags) {
    return (HANDLE)0x1234; // fake library handle
}
HANDLE LoadLibraryW(const void* lpFileName) { return (HANDLE)0x1234; }
void* GetProcAddress(HANDLE hModule, const char* lpProcName) { return 0; } // function not found

// --- Performance counter ---
int QueryPerformanceCounter(int64_t* ticks) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    *ticks = ts.tv_sec * 1000000000LL + ts.tv_nsec;
    return 1;
}

// --- NLS string search (return "not found") ---
int FindNLSString(int locale, uint32_t flags, const void* src, int srcCount,
                  const void* find, int findCount, int* found) {
    *found = 0;
    return -1; // CSTR_LESS_THAN means not found
}

int FindStringOrdinal(uint32_t flags, const void* src, int srcCount,
                      const void* find, int findCount, int ignoreCase) {
    return -1; // not found
}

// --- Locale functions ---
int LCIDToLocaleName(uint32_t locale, void* localeName, int localeNameSize, int flags) {
    // Return empty string (0 chars written) — BC will fall back to invariant culture
    if (localeName && localeNameSize > 0) {
        ((uint16_t*)localeName)[0] = 0; // null-terminate UTF-16
    }
    return 0;
}

int LocaleNameToLCID(const void* localeName, int flags) {
    return 0; // LOCALE_INVARIANT
}

// --- Authentication ---
int LogonUserW(const void* username, const void* domain, const void* password,
               uint32_t logonType, uint32_t logonProvider, HANDLE* phToken) {
    *phToken = (HANDLE)0xAA00; // fake token
    return 1; // success
}

// --- Tick count ---
uint64_t GetTickCount64(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)(ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
}

// --- Computer name (DnsHelper on v26 needs this) ---
int GetComputerNameExW(int format, uint16_t* buffer, int* size) {
    // Return a simple hostname in UTF-16LE
    const char* name = "bcserver";
    int len = 8; // strlen("bcserver")
    if (buffer == 0 || *size < len + 1) {
        *size = len + 1;
        return 0; // ERROR_MORE_DATA triggers retry with correct buffer size
    }
    for (int i = 0; i < len; i++) buffer[i] = (uint16_t)name[i];
    buffer[len] = 0;
    *size = len;
    return 1; // success
}

// --- General ---
uint32_t GetLastError(void) { return 0; }
HANDLE GetCurrentProcess(void) { return (HANDLE)-1; }
uint32_t FormatMessageW(uint32_t a, const void* b, uint32_t c, uint32_t d,
                        void* e, uint32_t f, void* g) { return 0; }

// =============================================================================
// user32.dll — OEM/ANSI encoding + keyboard layout functions
// =============================================================================

// Identity mapping: on Linux with UTF-8, OEM ≡ ANSI. Leave buffers unchanged.
int OemToCharBuffA(const uint8_t* src, uint8_t* dst, int size) {
    if (src != dst) memcpy(dst, src, size);
    return 1;
}

int CharToOemBuffA(const uint8_t* src, uint8_t* dst, int size) {
    if (src != dst) memcpy(dst, src, size);
    return 1;
}

// Keyboard layout functions — used by Microsoft.Dynamics.Framework.UI for
// keyboard mapping. Return US English defaults on Linux.
// GetKeyboardLayoutName: writes KL identifier string (e.g. "00000409" = US English)
int GetKeyboardLayoutNameW(uint16_t* pwszKLID) {
    // "00000409" in UTF-16LE (US English keyboard)
    const uint16_t us[] = {'0','0','0','0','0','4','0','9', 0};
    memcpy(pwszKLID, us, sizeof(us));
    return 1;
}
// Alias — .NET marshaling may use either name
int GetKeyboardLayoutName(uint16_t* pwszKLID) {
    return GetKeyboardLayoutNameW(pwszKLID);
}

// LoadKeyboardLayout: returns a dummy HKL handle
intptr_t LoadKeyboardLayoutW(const uint16_t* pwszKLID, uint32_t Flags) {
    return (intptr_t)0x04090409; // US English HKL
}
intptr_t LoadKeyboardLayout(const uint16_t* pwszKLID, uint32_t Flags) {
    return LoadKeyboardLayoutW(pwszKLID, Flags);
}

// GetKeyState: returns 0 (key not pressed)
int16_t GetKeyState(int nVirtKey) { return 0; }

// MapVirtualKeyEx: returns 0 (no mapping)
uint32_t MapVirtualKeyExW(uint32_t uCode, uint32_t uMapType, intptr_t dwhkl) { return 0; }
uint32_t MapVirtualKeyEx(uint32_t uCode, uint32_t uMapType, intptr_t dwhkl) { return 0; }

// ToUnicodeEx: returns 0 (no translation)
int ToUnicodeEx(uint32_t wVirtKey, uint32_t wScanCode, const uint8_t* lpKeyState,
                uint16_t* pwszBuff, int cchBuff, uint32_t wFlags, intptr_t dwhkl) {
    return 0;
}

// =============================================================================
// Wintrust.dll — Code signing verification
// =============================================================================

// Return TRUST_E_PROVIDER_UNKNOWN (0x800B0001) to skip verification gracefully
uint32_t WinVerifyTrust(HANDLE hwnd, HANDLE actionId, HANDLE trustData) {
    return 0; // S_OK = trusted (skip verification in test mode)
}

// =============================================================================
// rpcrt4.dll — RPC runtime
// =============================================================================

// Generate a sequential UUID using /dev/urandom
int UuidCreateSequential(void* guid) {
    // Fill 16 bytes of GUID with pseudo-random data
    FILE* f = fopen("/dev/urandom", "rb");
    if (f) {
        fread(guid, 16, 1, f);
        fclose(f);
    }
    return 0; // RPC_S_OK
}

// =============================================================================
// nclcsrts.dll — BC native runtime (SPN registration)
// =============================================================================

uint32_t NCL_SpnRegister(const void* a, const void* b, const void* c, int d) {
    return 0; // success
}

// =============================================================================
// dhcpcsvc.dll — DHCP client (not needed for test pipeline)
// =============================================================================

uint32_t DhcpCApiInitialize(uint32_t* version) { *version = 1; return 0; }
void DhcpCApiCleanup(void) {}
uint32_t DhcpRequestParams(uint32_t flags, HANDLE reserved, const void* adapter,
                           HANDLE classId, void* send, void* recd,
                           HANDLE buffer, uint32_t* size, const void* reqId) {
    return 1; // ERROR_INVALID_FUNCTION
}

// =============================================================================
// Netapi32.dll — Network management API
// =============================================================================

uint32_t DsGetDcName(const void* computer, const void* domain, HANDLE guid,
                     const void* site, int flags, HANDLE* info) {
    *info = 0;
    return 1355; // ERROR_NO_SUCH_DOMAIN
}

int NetApiBufferFree(HANDLE buffer) { return 0; } // NERR_Success

// =============================================================================
// ntdsapi.dll — Active Directory services
// =============================================================================

uint32_t DsBind(const void* dc, const void* domain, HANDLE* phDS) {
    *phDS = 0;
    return 1355; // ERROR_NO_SUCH_DOMAIN
}
uint32_t DsUnBind(HANDLE* phDS) { *phDS = 0; return 0; }
uint32_t DsCrackNames(HANDLE hDS, int flags, int offered, int desired,
                      uint32_t count, const void** names, HANDLE* result) {
    *result = 0;
    return 1355;
}
void DsFreeNameResult(HANDLE result) {}
uint32_t DsWriteAccountSpn(HANDLE hDS, int op, const void* acct,
                           uint32_t count, const void** spns) {
    return 1355;
}

// =============================================================================
// libgdiplus — System.Drawing.Common on Linux (stub for font enumeration)
// =============================================================================

typedef int GpStatus;
typedef struct { uint32_t GdiplusVersion; void* DebugEventCallback; int SuppressBackgroundThread; int SuppressExternalCodecs; } GdiplusStartupInput;
typedef struct { void* NotificationHook; void* NotificationUnhook; } GdiplusStartupOutput;

GpStatus GdiplusStartup(HANDLE* token, const GdiplusStartupInput* input, GdiplusStartupOutput* output) {
    *token = (HANDLE)1;
    if (output) { output->NotificationHook = 0; output->NotificationUnhook = 0; }
    return 0; // Ok
}
void GdiplusShutdown(HANDLE token) {}
GpStatus GdipNewInstalledFontCollection(HANDLE* fontCollection) {
    *fontCollection = (HANDLE)0xF0F0;
    return 0;
}
GpStatus GdipGetFontCollectionFamilyCount(HANDLE fontCollection, int* count) {
    *count = 0; // No fonts
    return 0;
}
GpStatus GdipGetFontCollectionFamilyList(HANDLE fontCollection, int max, HANDLE* families, int* count) {
    *count = 0;
    return 0;
}

// =============================================================================
// httpapi.dll — HTTP Server API (HttpSys replacement stubs)
// =============================================================================

// HTTP_INITIALIZE_SERVER = 1, HTTP_INITIALIZE_CONFIG = 2
uint32_t HttpInitialize(uint32_t version, uint32_t flags, void* reserved) { return 0; } // NO_ERROR
uint32_t HttpTerminate(uint32_t flags, void* reserved) { return 0; }

uint32_t HttpCreateServerSession(uint32_t version, uint64_t* sessionId, uint32_t reserved) {
    *sessionId = 0x1234;
    return 0;
}
uint32_t HttpCloseServerSession(uint64_t sessionId) { return 0; }

uint32_t HttpCreateUrlGroup(uint64_t sessionId, uint64_t* groupId, uint32_t reserved) {
    *groupId = 0x5678;
    return 0;
}
uint32_t HttpCloseUrlGroup(uint64_t groupId) { return 0; }

uint32_t HttpSetUrlGroupProperty(uint64_t groupId, int property, void* info, uint32_t infoLen) {
    return 0;
}

uint32_t HttpAddUrlToUrlGroup(uint64_t groupId, const void* url, uint64_t context, uint32_t reserved) {
    return 0;
}
uint32_t HttpRemoveUrlFromUrlGroup(uint64_t groupId, const void* url, uint32_t flags) {
    return 0;
}

uint32_t HttpCreateRequestQueue(uint32_t version, const void* name, void* securityAttributes,
                                uint32_t flags, HANDLE* requestQueueHandle) {
    *requestQueueHandle = (HANDLE)0xABCD;
    return 0;
}
uint32_t HttpCloseRequestQueue(HANDLE requestQueueHandle) { return 0; }

uint32_t HttpSetRequestQueueProperty(HANDLE requestQueue, int property,
                                     void* info, uint32_t infoLen, uint32_t reserved, void* overlapped) {
    return 0;
}

uint32_t HttpReceiveHttpRequest(HANDLE requestQueue, uint64_t requestId, uint32_t flags,
                                void* requestBuffer, uint32_t requestBufferLen,
                                uint32_t* bytesReturned, void* overlapped) {
    return 87; // ERROR_INVALID_PARAMETER — will cause async wait
}

uint32_t HttpSendHttpResponse(HANDLE requestQueue, uint64_t requestId, uint32_t flags,
                              void* response, void* cachePolicy, uint32_t* bytesSent,
                              void* reserved1, uint32_t reserved2, void* overlapped, void* logData) {
    if (bytesSent) *bytesSent = 0;
    return 0;
}

uint32_t HttpWaitForDisconnectEx(HANDLE requestQueue, uint64_t connectionId,
                                 uint32_t reserved, void* overlapped) {
    return 997; // ERROR_IO_PENDING
}

uint32_t HttpSetServerSessionProperty(uint64_t sessionId, int property, void* info, uint32_t infoLen) {
    return 0;
}

// =============================================================================
// advapi32.dll — Security/Registry (may be needed later)
// =============================================================================

// --- Registry access (SqlClient needs this for connection string parsing) ---
int RegOpenKeyExW(HANDLE hKey, const void* subKey, uint32_t options, uint32_t samDesired, HANDLE* result) {
    *result = 0;
    return 2; // ERROR_FILE_NOT_FOUND — key doesn't exist
}

int RegQueryValueExW(HANDLE hKey, const void* valueName, uint32_t* reserved,
                     uint32_t* type, void* data, uint32_t* cbData) {
    return 2; // ERROR_FILE_NOT_FOUND
}

int RegCloseKey(HANDLE hKey) { return 0; }

HANDLE RegisterEventSourceW(const void* server, const void* source) {
    return (HANDLE)0xEEEE;
}
int ReportEventW(HANDLE h, uint16_t type, uint16_t cat, uint32_t id,
                 void* sid, uint16_t numStrings, uint32_t dataSize,
                 const void** strings, void* rawData) {
    return 1;
}
int DeregisterEventSource(HANDLE h) { return 1; }
