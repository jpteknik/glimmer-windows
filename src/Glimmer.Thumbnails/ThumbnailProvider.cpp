// New file, no Swift original. The Mac shows a .glimmer document's picture in Finder with a QuickLook extension that
// reads the package's QuickLook/Thumbnail.png. This is the same for File Explorer: a thumbnail handler (a COM object
// implementing IInitializeWithStream and IThumbnailProvider, the shell's interfaces for it) that reads the PNG the
// document already carries, stored uncompressed in its ZIP (glimmer_zip.c), and gives Explorer a bitmap of it.
// QuickLook/Preview.png (1600 px) is used when Explorer asks for more than the thumbnail's 512 px.
//
// Windows Imaging Component decodes and scales the PNG; the bitmap is 32-bit BGRA with straight alpha, given as
// WTSAT_ARGB, as Microsoft's thumbnail provider sample (RecipeThumbnailProvider) does. The document is read through the IStream Explorer hands
// over and never written. Explorer runs thumbnail handlers in a separate surrogate process, so a failure here can't
// take Explorer down; any failure simply gives no thumbnail.
//
// Built by build.sh with llvm-mingw; the app (src/Glimmer.App) carries the DLLs and registers them for the current user.
#define WIN32_LEAN_AND_MEAN
#include <initguid.h>
#include <windows.h>
#include <objbase.h>
#include <shlwapi.h>
#include <propsys.h>
#include <thumbcache.h>
#include <wincodec.h>
#include <new>

#include "glimmer_zip.h"

// {bccdb912-b48b-4f71-ae30-b3dda8d16651}: Glimmer's thumbnail handler (Platform/ThumbnailHandler.cs registers it).
static const CLSID CLSID_GlimmerThumbnails = {0xbccdb912, 0xb48b, 0x4f71, {0xae, 0x30, 0xb3, 0xdd, 0xa8, 0xd1, 0x66, 0x51}};

static LONG live = 0;   // objects alive plus LockServer locks

namespace
{
const uint32_t LargestPng = 64u * 1024 * 1024;

int ReadAt(void *context, uint64_t offset, void *buffer, uint32_t size)
{
    IStream *stream = static_cast<IStream *>(context);
    LARGE_INTEGER to;
    to.QuadPart = static_cast<LONGLONG>(offset);
    if (FAILED(stream->Seek(to, STREAM_SEEK_SET, nullptr))) return 0;
    uint8_t *p = static_cast<uint8_t *>(buffer);
    while (size > 0)
    {
        ULONG got = 0;
        HRESULT hr = stream->Read(p, size, &got);
        if (FAILED(hr) || got == 0) return 0;
        p += got;
        size -= got;
    }
    return 1;
}

// The bytes of `name` from the document, or nothing.
HRESULT ReadEntry(IStream *stream, uint64_t size, const char *name, uint8_t **bytes, uint32_t *length)
{
    uint64_t offset = 0;
    uint32_t count = 0;
    if (!glimmer_zip_find_stored(size, ReadAt, stream, name, LargestPng, &offset, &count) || count == 0) return E_FAIL;
    uint8_t *data = static_cast<uint8_t *>(CoTaskMemAlloc(count));
    if (!data) return E_OUTOFMEMORY;
    if (!ReadAt(stream, offset, data, count))
    {
        CoTaskMemFree(data);
        return E_FAIL;
    }
    *bytes = data;
    *length = count;
    return S_OK;
}

template <class T> void Drop(T *&p)
{
    if (p)
    {
        p->Release();
        p = nullptr;
    }
}

// The PNG decoded, scaled to fit `side` (never enlarged), as a top-down 32-bit BGRA DIB section (straight alpha).
HRESULT Bitmap(const uint8_t *png, uint32_t length, UINT side, HBITMAP *result)
{
    IWICImagingFactory *factory = nullptr;
    IStream *stream = nullptr;
    IWICBitmapDecoder *decoder = nullptr;
    IWICBitmapFrameDecode *frame = nullptr;
    IWICBitmapScaler *scaler = nullptr;
    IWICFormatConverter *converter = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_IWICImagingFactory, reinterpret_cast<void **>(&factory));
    if (SUCCEEDED(hr))
    {
        stream = SHCreateMemStream(png, length);
        hr = stream ? S_OK : E_OUTOFMEMORY;
    }
    if (SUCCEEDED(hr)) hr = factory->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
    if (SUCCEEDED(hr)) hr = decoder->GetFrame(0, &frame);
    UINT width = 0, height = 0;
    if (SUCCEEDED(hr)) hr = frame->GetSize(&width, &height);
    if (SUCCEEDED(hr) && (width == 0 || height == 0)) hr = E_FAIL;
    IWICBitmapSource *source = frame;
    UINT w = width, h = height;
    if (SUCCEEDED(hr) && side > 0 && (width > side || height > side))
    {
        if (width >= height)
        {
            w = side;
            h = static_cast<UINT>((static_cast<unsigned long long>(height) * side + width / 2) / width);
        }
        else
        {
            h = side;
            w = static_cast<UINT>((static_cast<unsigned long long>(width) * side + height / 2) / height);
        }
        if (w == 0) w = 1;
        if (h == 0) h = 1;
        hr = factory->CreateBitmapScaler(&scaler);
        if (SUCCEEDED(hr)) hr = scaler->Initialize(frame, w, h, WICBitmapInterpolationModeFant);
        if (SUCCEEDED(hr)) source = scaler;
    }
    if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&converter);
    if (SUCCEEDED(hr))
        hr = converter->Initialize(source, GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
    if (SUCCEEDED(hr))
    {
        BITMAPINFO info = {};
        info.bmiHeader.biSize = sizeof(info.bmiHeader);
        info.bmiHeader.biWidth = static_cast<LONG>(w);
        info.bmiHeader.biHeight = -static_cast<LONG>(h);   // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        void *bits = nullptr;
        HBITMAP bitmap = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        hr = bitmap && bits ? S_OK : E_OUTOFMEMORY;
        if (SUCCEEDED(hr)) hr = converter->CopyPixels(nullptr, w * 4, w * h * 4, static_cast<BYTE *>(bits));
        if (SUCCEEDED(hr)) *result = bitmap;
        else if (bitmap) DeleteObject(bitmap);
    }
    Drop(converter);
    Drop(scaler);
    Drop(frame);
    Drop(decoder);
    Drop(stream);
    Drop(factory);
    return hr;
}

class ThumbnailProvider final : public IInitializeWithStream, public IThumbnailProvider
{
    LONG references = 1;
    IStream *document = nullptr;

public:
    ThumbnailProvider() { InterlockedIncrement(&live); }
    ThumbnailProvider(const ThumbnailProvider &) = delete;
    ThumbnailProvider &operator=(const ThumbnailProvider &) = delete;

    ~ThumbnailProvider()
    {
        Drop(document);
        InterlockedDecrement(&live);
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void **object) override
    {
        if (!object) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IInitializeWithStream)) *object = static_cast<IInitializeWithStream *>(this);
        else if (IsEqualIID(riid, IID_IThumbnailProvider)) *object = static_cast<IThumbnailProvider *>(this);
        else
        {
            *object = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references)); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG left = InterlockedDecrement(&references);
        if (left == 0) delete this;
        return static_cast<ULONG>(left);
    }

    IFACEMETHODIMP Initialize(IStream *stream, DWORD) override
    {
        if (!stream) return E_INVALIDARG;
        if (document) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        document = stream;
        document->AddRef();
        return S_OK;
    }

    IFACEMETHODIMP GetThumbnail(UINT side, HBITMAP *bitmap, WTS_ALPHATYPE *alpha) override
    {
        if (!bitmap || !alpha) return E_POINTER;
        *bitmap = nullptr;
        *alpha = WTSAT_UNKNOWN;
        if (!document) return E_UNEXPECTED;
        STATSTG stat = {};
        if (FAILED(document->Stat(&stat, STATFLAG_NONAME))) return E_FAIL;
        uint8_t *png = nullptr;
        uint32_t length = 0;
        // The 512 px thumbnail for Explorer's usual sizes, the 1600 px preview for larger ones (and when the thumbnail
        // is missing).
        HRESULT hr = side > 512 ? ReadEntry(document, stat.cbSize.QuadPart, "QuickLook/Preview.png", &png, &length) : E_FAIL;
        if (FAILED(hr)) hr = ReadEntry(document, stat.cbSize.QuadPart, "QuickLook/Thumbnail.png", &png, &length);
        if (FAILED(hr)) hr = ReadEntry(document, stat.cbSize.QuadPart, "QuickLook/Preview.png", &png, &length);
        if (FAILED(hr)) return hr;
        hr = Bitmap(png, length, side, bitmap);
        CoTaskMemFree(png);
        if (SUCCEEDED(hr)) *alpha = WTSAT_ARGB;
        return hr;
    }
};

class Factory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void **object) override
    {
        if (!object) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *object = static_cast<IClassFactory *>(this);
            AddRef();
            return S_OK;
        }
        *object = nullptr;
        return E_NOINTERFACE;
    }

    // The factory is a static object that lives as long as the DLL, but a reference to it still keeps the DLL loaded:
    // DllCanUnloadNow says S_FALSE while anyone holds one (as Microsoft's sample counts its factory).
    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        InterlockedIncrement(&live);
        return 2;
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        InterlockedDecrement(&live);
        return 1;
    }

    IFACEMETHODIMP CreateInstance(IUnknown *outer, REFIID riid, void **object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        ThumbnailProvider *provider = new (std::nothrow) ThumbnailProvider();
        if (!provider) return E_OUTOFMEMORY;
        HRESULT hr = provider->QueryInterface(riid, object);
        provider->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&live);
        else InterlockedDecrement(&live);
        return S_OK;
    }
};

Factory factory;
}

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void **object)
{
    if (!object) return E_POINTER;
    *object = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_GlimmerThumbnails)) return CLASS_E_CLASSNOTAVAILABLE;
    return factory.QueryInterface(riid, object);
}

extern "C" HRESULT WINAPI DllCanUnloadNow() { return live == 0 ? S_OK : S_FALSE; }

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
