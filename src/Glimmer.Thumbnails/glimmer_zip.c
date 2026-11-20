/* New file, no Swift original: see glimmer_zip.h. Follows the ZIP format as PKWARE's APPNOTE.TXT (6.3.10) describes it:
 * the end of central directory record (and, for ZIP64, its locator and the ZIP64 end record), the central directory,
 * and the entry's local header, whose name and extra lengths say where the data starts. */
#include "glimmer_zip.h"

/* Enough for the end record and the largest comment after it (22 + 65535 bytes). */
#define WINDOW 65600u

typedef struct
{
    glimmer_zip_read read;
    void *context;
    uint64_t size;
    uint64_t start;    /* where buf[0] is in the archive */
    uint32_t length;   /* how many bytes of buf hold the archive */
    uint8_t buf[WINDOW];
} reader;

static uint16_t u16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static uint32_t u32(const uint8_t *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static uint64_t u64(const uint8_t *p) { return (uint64_t)u32(p) | ((uint64_t)u32(p + 4) << 32); }

static int signature(const uint8_t *p, uint8_t a, uint8_t b) { return p[0] == 'P' && p[1] == 'K' && p[2] == a && p[3] == b; }

/* [at, at + count) of the archive, read into the window when it isn't there; 0 when it lies outside the archive or
 * can't be read. */
static const uint8_t *view(reader *r, uint64_t at, uint32_t count)
{
    if (count > WINDOW || at > r->size || count > r->size - at) return 0;
    if (r->length > 0 && at >= r->start && at - r->start <= r->length && count <= r->length - (uint32_t)(at - r->start))
        return r->buf + (at - r->start);
    uint64_t left = r->size - at;
    uint32_t want = left < WINDOW ? (uint32_t)left : WINDOW;
    r->length = 0;
    if (!r->read(r->context, at, r->buf, want)) return 0;
    r->start = at;
    r->length = want;
    return r->buf;
}

int glimmer_zip_find_stored(uint64_t archive_size, glimmer_zip_read read, void *context, const char *name,
                            uint32_t max_size, uint64_t *data_offset, uint32_t *data_size)
{
    reader r;
    r.read = read;
    r.context = context;
    r.size = archive_size;
    r.start = 0;
    r.length = 0;
    if (!read || !name || !data_offset || !data_size || archive_size < 22) return 0;
    uint32_t name_length = 0;
    while (name[name_length])
        if (++name_length > 4096) return 0;

    /* The end record: the last "PK\5\6" whose comment reaches exactly to the end of the archive. */
    uint32_t tail = archive_size < 22 + 65535 ? (uint32_t)archive_size : 22 + 65535;
    uint64_t tail_at = archive_size - tail;
    const uint8_t *t = view(&r, tail_at, tail);
    if (!t) return 0;
    int64_t found = -1;
    for (int64_t i = (int64_t)tail - 22; i >= 0; i--)
    {
        if (signature(t + i, 5, 6) && (uint64_t)i + 22 + u16(t + i + 20) == tail)
        {
            found = i;
            break;
        }
    }
    if (found < 0) return 0;
    uint64_t end_at = tail_at + (uint64_t)found;
    uint64_t entries = u16(t + found + 10);
    uint64_t directory_size = u32(t + found + 12);
    uint64_t directory_at = u32(t + found + 16);
    if (entries == 0xFFFFu || directory_size == 0xFFFFFFFFu || directory_at == 0xFFFFFFFFu)
    {
        /* ZIP64: a locator just before the end record points at the ZIP64 end record, which has the real values. */
        if (end_at < 20) return 0;
        const uint8_t *locator = view(&r, end_at - 20, 20);
        if (!locator || !signature(locator, 6, 7)) return 0;
        const uint8_t *end64 = view(&r, u64(locator + 8), 56);
        if (!end64 || !signature(end64, 6, 6)) return 0;
        entries = u64(end64 + 32);
        directory_size = u64(end64 + 40);
        directory_at = u64(end64 + 48);
    }
    if (directory_at > archive_size || directory_size > archive_size - directory_at) return 0;

    uint64_t at = directory_at, end = directory_at + directory_size;
    for (uint64_t k = 0; k < entries && end - at >= 46; k++)
    {
        const uint8_t *h = view(&r, at, 46);
        if (!h || !signature(h, 1, 2)) return 0;
        uint16_t flags = u16(h + 8), method = u16(h + 10);
        uint64_t compressed = u32(h + 20), uncompressed = u32(h + 24), local = u32(h + 42);
        uint32_t n = u16(h + 28), x = u16(h + 30), c = u16(h + 32);
        uint64_t next = at + 46 + n + x + c;
        if (next > end) return 0;
        if (n == name_length)
        {
            const uint8_t *entry = view(&r, at, 46 + n + x);
            if (!entry) return 0;
            int same = 1;
            for (uint32_t i = 0; i < n && same; i++) same = entry[46 + i] == (uint8_t)name[i];
            if (same)
            {
                if (compressed == 0xFFFFFFFFu || uncompressed == 0xFFFFFFFFu || local == 0xFFFFFFFFu)
                {
                    /* The ZIP64 extra field holds, in this order, the 64-bit value of each field that is all ones. */
                    const uint8_t *extra = entry + 46 + n;
                    uint32_t i = 0;
                    int read64 = 0;
                    while (!read64 && i + 4 <= x)
                    {
                        uint32_t id = u16(extra + i), length = u16(extra + i + 2);
                        if (i + 4 + length > x) return 0;
                        if (id == 1)
                        {
                            const uint8_t *v = extra + i + 4;
                            uint32_t used = 0;
                            if (uncompressed == 0xFFFFFFFFu) { if (used + 8 > length) return 0; uncompressed = u64(v + used); used += 8; }
                            if (compressed == 0xFFFFFFFFu) { if (used + 8 > length) return 0; compressed = u64(v + used); used += 8; }
                            if (local == 0xFFFFFFFFu) { if (used + 8 > length) return 0; local = u64(v + used); used += 8; }
                            read64 = 1;
                        }
                        i += 4 + length;
                    }
                    if (!read64) return 0;
                }
                /* Stored as it is, not encrypted, and small enough. */
                if (method != 0 || (flags & 1) || compressed != uncompressed || compressed > max_size) return 0;
                const uint8_t *header = view(&r, local, 30);
                if (!header || !signature(header, 3, 4)) return 0;
                uint64_t data = local + 30 + u16(header + 26) + u16(header + 28);
                if (data > archive_size || compressed > archive_size - data) return 0;
                *data_offset = data;
                *data_size = (uint32_t)compressed;
                return 1;
            }
        }
        at = next;
    }
    return 0;
}
