/*
 * Chromium stub symbols for libchromescreenai.so (Linux only).
 *
 * The Chrome-shipped libchromescreenai.so has unresolved references to a
 * handful of symbols that exist inside Chromium's own process image but not
 * on a vanilla Linux host.  They are never called during normal OCR, but the
 * dynamic linker still refuses to finish loading libchromescreenai.so until
 * they resolve somewhere.
 *
 * This tiny shared object exports them as no-ops.  Load it with
 * RTLD_GLOBAL *before* loading libchromescreenai.so.
 *
 * Build:
 *   cc -shared -fPIC -o _chromium_stubs.so stubs.c
 *
 * Ported directly from locro/_dll.py (original Python implementation).
 */

#include <stddef.h>

void *unsupported_gzopen(const char *path, const char *mode)
{
    (void)path;
    (void)mode;
    return NULL;
}

int unsupported_gzread(void *file, void *buf, unsigned len)
{
    (void)file;
    (void)buf;
    (void)len;
    return 0;
}

int unsupported_gzclose(void *file)
{
    (void)file;
    return 0;
}

/* threadlogger::EnableThreadedLogging(int) — C++ symbol, Itanium-mangled */
void _ZN12threadlogger21EnableThreadedLoggingEi(int x)
{
    (void)x;
}
