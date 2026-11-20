/* New file, no Swift original. Finds a file stored without compression in a ZIP archive, reading the archive through a
 * callback. Freestanding C: no library calls and no allocation, so the same code is built into the Windows thumbnail
 * handler (GlimmerThumbnails.dll) and, for its tests, into a Linux shared library the .NET tests load.
 *
 * A .glimmer document saved on Windows is a ZIP whose QuickLook/Thumbnail.png and QuickLook/Preview.png are stored as
 * they are (PackageContents.WriteZip compresses only the JSON). */
#ifndef GLIMMER_ZIP_H
#define GLIMMER_ZIP_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Reads `size` bytes at `offset` into `buffer`; returns 1 when all of them were read, 0 otherwise. */
typedef int (*glimmer_zip_read)(void *context, uint64_t offset, void *buffer, uint32_t size);

/* Finds the entry named exactly `name` (UTF-8, '/' between folders). Returns 1 with the offset and size of its data when
 * it is stored without compression and at most `max_size` bytes; 0 when the entry is missing, compressed, too large, or
 * the archive is damaged. ZIP64 archives are read too. */
int glimmer_zip_find_stored(uint64_t archive_size, glimmer_zip_read read, void *context, const char *name,
                            uint32_t max_size, uint64_t *data_offset, uint32_t *data_size);

#ifdef __cplusplus
}
#endif

#endif
