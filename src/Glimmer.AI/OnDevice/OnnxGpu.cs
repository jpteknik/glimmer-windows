// New file, no Swift original. The Mac runs Real-ESRGAN through Core ML, which puts it on the GPU or the Neural Engine by
// itself. ONNX Runtime reaches the graphics card through its WebGPU execution provider, a plug-in library
// (Microsoft.ML.OnnxRuntime.EP.WebGpu 0.6.0: Dawn over Direct3D 12 on Windows, over Vulkan on Linux). This file finds that
// library, registers it with ONNX Runtime once per process and picks the device; OnnxModels decides when to use it and
// falls back to the CPU.
//
// Measured on 2026-11-20 in WSL, where the only Vulkan device is Mesa's lavapipe (Vulkan computed on the CPU): all 73 nodes
// of Real-ESRGAN are placed on the provider, and a random 256 px tile differs from the CPU's by at most 4.7e-6 of the 0…1
// range. Nothing here has been run on a Windows graphics card yet.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

namespace Glimmer.AI;

internal static partial class OnnxGpu
{
    private const string RegistrationName = "glimmer_webgpu";
    private static readonly object Gate = new();
    private static bool registered;
    private static string? registrationFailure;

    /// <summary>For tests: the provider library to use instead of searching for it. A path that doesn't exist is a missing provider.</summary>
    internal static string? LibraryPathOverride { get; set; }

    /// <summary>The provider library's file name on this system.</summary>
    internal static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "onnxruntime_providers_webgpu.dll"
        : OperatingSystem.IsMacOS() ? "libonnxruntime_providers_webgpu.dylib"
        : "libonnxruntime_providers_webgpu.so";

    /// <summary>
    /// The WebGPU device to give a session, registering the provider first if this process hasn't. Null when there is
    /// none, with the reason in <paramref name="detail"/>; otherwise <paramref name="detail"/> describes the device.
    /// </summary>
    internal static OrtEpDevice? Device(out string detail)
    {
        var env = OrtEnv.Instance();
        lock (Gate)
        {
            var path = Locate();
            if (path is null)
            {
                detail = $"the graphics card library is missing ({LibraryFileName})";
                return null;
            }
            if (registrationFailure is not null)
            {
                detail = registrationFailure;
                return null;
            }
            if (!registered)
            {
                try
                {
                    if (OperatingSystem.IsWindows()) PrepareWindows(path);
                    env.RegisterExecutionProviderLibrary(RegistrationName, path);
                    registered = true;
                }
                catch (Exception e)
                {
                    // A library that is there but doesn't load won't load later either.
                    registrationFailure = "the graphics card library could not be loaded: " + e.Message;
                    detail = registrationFailure;
                    return null;
                }
            }
        }
        var devices = env.GetEpDevices().Where(d => d.EpName == WebGpuEp.GetEpName()).ToList();
        if (devices.Count == 0)
        {
            detail = "no graphics card for WebGPU";
            return null;
        }
        var device = Preferred(devices);
        detail = Describe(device);
        return device;
    }

    /// <summary>
    /// The provider library: in a native-library folder the runtime lists (a single-file app extracts its native
    /// libraries to a folder of its own at start-up and lists it there; <c>dotnet test</c> lists runtimes/&lt;rid&gt;/native),
    /// next to the app, under runtimes/&lt;rid&gt;/native, and last where the package's own helper looks (beside its
    /// assembly, which a single-file app doesn't have on disk). Null when it is nowhere.
    /// </summary>
    internal static string? Locate()
    {
        if (LibraryPathOverride is { } forced) return File.Exists(forced) ? forced : null;
        foreach (var folder in Folders())
        {
            var path = Path.Combine(folder, LibraryFileName);
            if (File.Exists(path)) return path;
        }
        try { return WebGpuEp.GetLibraryPath(); }
        catch (Exception) { return null; }
    }

    private static IEnumerable<string> Folders()
    {
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string listed)
        {
            foreach (var folder in listed.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return folder;
        }
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeId, "native");
    }

    private static string RuntimeId =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>
    /// The high-performance adapter when ONNX Runtime's device discovery says which it is (on Windows it lists each
    /// GPU's place in DXGI's high-performance order and whether it is discrete), otherwise the first device.
    /// </summary>
    private static OrtEpDevice Preferred(List<OrtEpDevice> devices) =>
        devices.OrderBy(d => Meta(d, "Discrete") is "1" or "true" or "True" ? 0 : 1)
               .ThenBy(d => int.TryParse(Meta(d, "DxgiHighPerformanceIndex"), out int index) ? index : int.MaxValue)
               .First();

    private static string? Meta(OrtEpDevice device, string key) =>
        device.HardwareDevice.Metadata.Entries.TryGetValue(key, out var value) ? value : null;

    /// <summary>What ONNX Runtime knows of the device, e.g. "NVIDIA (10DE:2786), WebGPU".</summary>
    private static string Describe(OrtEpDevice device)
    {
        var hardware = device.HardwareDevice;
        string name = Meta(device, "Description") ?? (string.IsNullOrWhiteSpace(hardware.Vendor) ? "GPU" : hardware.Vendor);
        return $"{name} ({hardware.VendorId:X4}:{hardware.DeviceId:X4}), WebGPU";
    }

    /// <summary>
    /// Two things before the provider is loaded on Windows.
    ///
    /// Dawn, inside the provider, loads the DirectX Shader Compiler by name, and the folder a single-file app extracts
    /// to is not on the DLL search path: dxcompiler.dll is loaded from beside the provider first (Windows finds a module
    /// that is already loaded by its name).
    ///
    /// The provider carries ONNX Runtime's 1DS telemetry client, which uploads usage events to Microsoft over HTTPS and
    /// keeps a device id (the 0.6.0 binary holds mobile.events.data.microsoft.com and imports WinHTTP). ONNX Runtime's
    /// CPU library for Windows has no such client: it writes ETW events, which Windows handles under its own
    /// diagnostic-data settings. Glimmer adds no network traffic nobody asked for, so ORT_DISABLE_TELEMETRY is set unless
    /// the person has set it; ONNX Runtime's Privacy.md says that variable, set before the library starts, keeps the
    /// uploader, the events and the device id from being created. The provider reads its environment both through Win32
    /// and through the C runtime's own copy (it imports GetEnvironmentVariable and _dupenv_s), so both are set. On Linux,
    /// where only the tests run, their run settings set the variable before the process starts.
    /// </summary>
    private static void PrepareWindows(string provider)
    {
        var compiler = Path.Combine(Path.GetDirectoryName(provider)!, "dxcompiler.dll");
        if (File.Exists(compiler)) NativeLibrary.TryLoad(compiler, out _);
        if (Environment.GetEnvironmentVariable("ORT_DISABLE_TELEMETRY") is null)
        {
            Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
            try { _ = PutEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1"); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    /// <summary>The C runtime's <c>_putenv_s</c> (ucrtbase.dll, part of Windows 10 and later).</summary>
    [LibraryImport("ucrtbase.dll", EntryPoint = "_putenv_s", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int PutEnvironmentVariable(string name, string value);
}
