// Launch a packaged application and record every frame its window presents (Windows.Graphics.Capture).
// A frame is delivered only when the window content changes, with the system time of its presentation.
//
// Usage: WindowCapture <AppUserModelId> <window title> <output dir> [seconds]
// Output: <output dir>\<ms since launch>_<HHmmss_fff>.png and timeline.csv

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinRT;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: WindowCapture <AppUserModelId> <window title> <output dir> [seconds]");
    return 1;
}
var aumid = args[0];
var title = args[1];
var outputDir = Path.GetFullPath(args[2]);
var seconds = args.Length > 3 ? int.Parse(args[3]) : 60;
Directory.CreateDirectory(outputDir);

// --- Cold start: stop the running instance -------------------------------------------------------
var packageFamily = aumid.Split('!')[0];
foreach (var process in Process.GetProcesses())
{
    try
    {
        var path = process.MainModule?.FileName ?? "";
        if (path.Contains(packageFamily.Split('_')[0] + "_", StringComparison.OrdinalIgnoreCase) && path.Contains("\\WindowsApps\\"))
        {
            process.Kill();
            process.WaitForExit(5000);
        }
    }
    catch
    {
        // Access denied on system processes
    }
}
Thread.Sleep(500);

// --- Launch through the activation manager (same path as the Start menu) ----------------------------
var launch = DateTime.Now;
var activationManager = (IApplicationActivationManager)new ApplicationActivationManager();
Marshal.ThrowExceptionForHR(activationManager.ActivateApplication(aumid, null, 0, out var pid));
Console.WriteLine($"{launch:HH:mm:ss.fff} launched {aumid} (pid {pid})");

// --- Wait for the window ----------------------------------------------------------------------------
IntPtr window = IntPtr.Zero;
while (window == IntPtr.Zero && (DateTime.Now - launch).TotalSeconds < 15)
{
    window = Native.FindWindow(title);
    if (window == IntPtr.Zero) Thread.Sleep(10);
}
if (window == IntPtr.Zero)
{
    Console.Error.WriteLine($"Window '{title}' not found");
    return 2;
}
Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} window found (+{(DateTime.Now - launch).TotalMilliseconds:F0} ms)");
// A launch from a background console does not get the foreground, the Start menu launch does
Native.BringToForeground(window);
Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} foreground={Native.GetForegroundWindow() == window}");

// --- Capture ----------------------------------------------------------------------------------------
var device = Native.CreateDirect3DDevice();
var item = Native.CreateItemForWindow(window);
var timeline = new List<string> { "time,ms_since_launch,width,height,foreground,file" };
var gate = new object();
var frameIndex = 0;
using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
framePool.FrameArrived += (pool, _) =>
{
    using var frame = pool.TryGetNextFrame();
    if (frame == null) return;
    // SystemRelativeTime is in QPC units (100 ns) like Stopwatch timestamps converted to TimeSpan
    var nowQpc = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    var presented = DateTime.Now - (nowQpc - frame.SystemRelativeTime);
    var ms = (int)(presented - launch).TotalMilliseconds;
    var file = $"{ms:D6}ms_{presented:HHmmss_fff}.png";
    var foreground = Native.GetForegroundWindow() == window ? 1 : 0;
    using (var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult())
    {
        Native.SavePng(bitmap, Path.Combine(outputDir, file));
    }
    lock (gate)
    {
        timeline.Add($"{presented:HH:mm:ss.fff},{ms},{frame.ContentSize.Width},{frame.ContentSize.Height},{foreground},{file}");
        frameIndex++;
    }
};
using var session = framePool.CreateCaptureSession(item);
session.IsCursorCaptureEnabled = false;
session.StartCapture();
Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} capturing for {seconds} s...");
Thread.Sleep(TimeSpan.FromSeconds(seconds) - (DateTime.Now - launch));
session.Dispose();
lock (gate)
{
    File.WriteAllLines(Path.Combine(outputDir, "timeline.csv"), timeline);
    Console.WriteLine($"{frameIndex} frames -> {outputDir}");
}
return 0;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IApplicationActivationManager
{
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
class ApplicationActivationManager
{
}

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

static class Native
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public static void BringToForeground(IntPtr window)
    {
        const byte VK_MENU = 0x12;
        const uint KEYEVENTF_KEYUP = 0x2;
        for (var attempt = 0; attempt < 5 && GetForegroundWindow() != window; attempt++)
        {
            // The foreground lock is released for the process that sent the last input
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(window);
            Thread.Sleep(50);
        }
    }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IntPtr FindWindow(string title)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var text = new StringBuilder(256);
            var className = new StringBuilder(256);
            GetWindowText(hwnd, text, text.Capacity);
            GetClassName(hwnd, className, className.Capacity);
            if (text.ToString() == title && className.ToString() == "ApplicationFrameWindow")
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IDirect3DDevice CreateDirect3DDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, 7, out var d3dDevice, out _, out var context));
        var dxgiDeviceIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, ref dxgiDeviceIid, out var dxgiDevice));
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        Marshal.Release(dxgiDevice);
        Marshal.Release(context);
        Marshal.Release(d3dDevice);
        return device;
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var pointer = interop.CreateForWindow(window, ref iid);
        var item = GraphicsCaptureItem.FromAbi(pointer);
        Marshal.Release(pointer);
        return item;
    }

    public static void SavePng(SoftwareBitmap bitmap, string path)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        var bytes = new byte[stream.Size];
        reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
        reader.ReadBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }
}
