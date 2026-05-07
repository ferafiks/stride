// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using WinRT;

// Disambiguate from Silk.NET.* counterparts.
using IDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using DirectXPixelFormat = Windows.Graphics.DirectX.DirectXPixelFormat;

namespace Stride.GameStudio.AutoTesting;

/// <summary>
/// Captures a top-level window's pixel content via Windows.Graphics.Capture (WGC). WGC reads from
/// the DWM compositor's output, so the source can be drawn by any graphics API — D3D11/12, Vulkan,
/// GDI — and DComp content (WPF chrome, AvalonDock panels) is captured correctly. The yellow capture
/// border and cursor are disabled where the OS supports it (Win11 22H2 / Win10 1903+).
/// </summary>
internal static class GraphicsCaptureClient
{
    private const string GraphicsCaptureSessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";

    [ComImport, System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref System.Guid iid, out IntPtr value);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref System.Guid iid, out IntPtr value);
    }

    [ComImport, System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref System.Guid iid, out IntPtr ptr);
    }

    // IID of IGraphicsCaptureItem; pinned because typeof(GraphicsCaptureItem).GUID is projection-
    // version dependent.
    private static readonly System.Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    private const int SW_RESTORE = 9;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    private const int WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtrW(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetWindowDisplayAffinity(IntPtr hwnd, out uint dwAffinity);

    private const int GWLP_HWNDPARENT = -8;
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const long WS_EX_APPWINDOW = 0x00040000L;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtrW(IntPtr hwnd, int nIndex, long dwNewLong);

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmFlush();

    private static readonly string DiagLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gs-diag.log");
    private static void DiagLog(string message)
    {
        try { System.IO.File.AppendAllText(DiagLogPath, $"{DateTime.UtcNow:HH:mm:ss.fff} [tid={System.Threading.Thread.CurrentThread.ManagedThreadId}] WGC: {message}\n"); }
        catch { }
    }

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr classId, in System.Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString([MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    private static T GetActivationFactory<T>(string runtimeClassName) where T : class
    {
        WindowsCreateString(runtimeClassName, (uint)runtimeClassName.Length, out var hstring);
        try
        {
            var iid = typeof(T).GUID;
            RoGetActivationFactory(hstring, in iid, out var factoryPtr);
            try { return (T)Marshal.GetObjectForIUnknown(factoryPtr); }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { WindowsDeleteString(hstring); }
    }

    public static unsafe Task CaptureToPngAsync(IntPtr hwnd, string path)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("HWND is zero.", nameof(hwnd));

        // 1. HWND → GraphicsCaptureItem via the activation factory's IGraphicsCaptureItemInterop.
        var itemFactory = GetActivationFactory<IGraphicsCaptureItemInterop>("Windows.Graphics.Capture.GraphicsCaptureItem");
        var itemIid = IID_IGraphicsCaptureItem;
        var createHr = itemFactory.CreateForWindow(hwnd, ref itemIid, out var itemAbi);

        // WGC rejects owned/transient/no-AppWindow windows (e.g. AvalonDock floating panels) with
        // E_INVALIDARG — see Microsoft's IsCapturableWindow sample. Promote temporarily: clear
        // owner and add WS_EX_APPWINDOW. The window must stay promoted through capture, so we
        // hand the original style/owner to the async helper to restore in its finally block.
        long origExStyle = 0, origOwner = 0;
        var promoted = false;
        if (createHr == E_INVALIDARG)
        {
            origExStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            origOwner = GetWindowLongPtrW(hwnd, GWLP_HWNDPARENT);
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, origExStyle | WS_EX_APPWINDOW);
            SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, 0);
            promoted = true;
            DiagLog($"WGC E_INVALIDARG; promoted hwnd 0x{hwnd.ToInt64():X} (cleared owner 0x{origOwner:X}, added WS_EX_APPWINDOW)");
            createHr = itemFactory.CreateForWindow(hwnd, ref itemIid, out itemAbi);
            if (createHr < 0) DiagLog($"CreateForWindow (after promotion) hr=0x{createHr:X8}");
        }
        if (createHr < 0)
        {
            if (promoted)
            {
                SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, origOwner);
                SetWindowLongPtrW(hwnd, GWL_EXSTYLE, origExStyle);
            }
            Marshal.ThrowExceptionForHR(createHr);
        }
        GraphicsCaptureItem item;
        try { item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemAbi); }
        finally { Marshal.Release(itemAbi); }

        // 2. Create a D3D11 device. BGRA support is required for the WGC framepool.
        var d3d11 = D3D11.GetApi(null);
        ID3D11Device* devicePtr = null;
        ID3D11DeviceContext* contextPtr = null;
        D3DFeatureLevel level = 0;
        HResult hr = d3d11.CreateDevice(
            pAdapter: null, DriverType: D3DDriverType.Hardware, Software: IntPtr.Zero,
            Flags: (uint)CreateDeviceFlag.BgraSupport,
            pFeatureLevels: null, FeatureLevels: 0,
            SDKVersion: D3D11.SdkVersion,
            ppDevice: ref devicePtr, pFeatureLevel: &level, ppImmediateContext: ref contextPtr);
        if (hr.IsFailure) throw Marshal.GetExceptionForHR(hr.Value)!;

        // 3. ID3D11Device → IDXGIDevice → IDirect3DDevice (WinRT).
        IDirect3DDevice graphicsDevice;
        IDXGIDevice* dxgiDevice = null;
        var dxgiIid = IDXGIDevice.Guid;
        SilkMarshal.ThrowHResult(devicePtr->QueryInterface(ref dxgiIid, (void**)&dxgiDevice));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice((IntPtr)dxgiDevice, out var graphicsDeviceUnk));
            try { graphicsDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsDeviceUnk); }
            finally { Marshal.Release(graphicsDeviceUnk); }
        }
        finally { dxgiDevice->Release(); }

        // 4. Framepool + session. CreateFreeThreaded dispatches FrameArrived on a threadpool
        //    thread; the regular Create requires a DispatcherQueue on the calling thread.
        var size = item.Size;
        DiagLog($"item.Size={size.Width}x{size.Height} hwnd=0x{hwnd.ToInt64():X}");
        if (size.Width <= 0 || size.Height <= 0)
        {
            // Fall back to GetClientRect-derived size — happens if WPF hasn't fully laid out yet.
            if (GetClientRect(hwnd, out var rect))
            {
                size.Width = rect.Right - rect.Left;
                size.Height = rect.Bottom - rect.Top;
                DiagLog($"WGC: fell back to GetClientRect → {size.Width}x{size.Height}");
            }
            if (size.Width <= 0 || size.Height <= 0)
                throw new InvalidOperationException($"GraphicsCaptureItem reports zero size and GetClientRect failed; window not yet realised.");
        }
        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            graphicsDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 1,
            size: size);
        var session = framePool.CreateCaptureSession(item);

        // Suppress yellow capture border (Win11 22H2+) and cursor overlay (Win10 1903+).
        if (ApiInformation.IsPropertyPresent(GraphicsCaptureSessionType, "IsBorderRequired"))
            session.IsBorderRequired = false;
        if (ApiInformation.IsPropertyPresent(GraphicsCaptureSessionType, "IsCursorCaptureEnabled"))
            session.IsCursorCaptureEnabled = false;

        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        int frameCallbackCount = 0;
        Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> handler = null!;
        handler = (sender, _) =>
        {
            var n = System.Threading.Interlocked.Increment(ref frameCallbackCount);
            var frame = sender.TryGetNextFrame();
            DiagLog($"FrameArrived #{n} frame={(frame is null ? "null" : $"{frame.ContentSize.Width}x{frame.ContentSize.Height}")}");
            if (frame is null) return;
            framePool.FrameArrived -= handler;
            tcs.TrySetResult(frame);
        };
        framePool.FrameArrived += handler;

        // Diagnostics: WS_EX_NOREDIRECTIONBITMAP excludes from DWM redirection (and so from WGC);
        // WDA_EXCLUDEFROMCAPTURE opts out of capture entirely.
        var exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        GetWindowDisplayAffinity(hwnd, out var affinity);
        DiagLog($"Styles: exStyle=0x{exStyle:X} NoRedirBitmap={(exStyle & WS_EX_NOREDIRECTIONBITMAP) != 0} affinity=0x{affinity:X} ExcludeFromCapture={affinity == WDA_EXCLUDEFROMCAPTURE}");

        // WGC only delivers FrameArrived when DWM presents new composition; nudge the window so
        // the first frame arrives.
        var fg = SetForegroundWindow(hwnd);
        var sw = ShowWindow(hwnd, SW_RESTORE);
        var rd = RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        DiagLog($"Nudge: SetForegroundWindow={fg} ShowWindow={sw} RedrawWindow={rd}");

        session.StartCapture();
        DiagLog("StartCapture returned");

        // DwmFlush blocks until DWM advances its composition — guarantees at least one frame
        // pass that WGC can pick up.
        try { DwmFlush(); DiagLog("DwmFlush returned"); }
        catch (Exception ex) { DiagLog($"DwmFlush threw: {ex.Message}"); }

        // Hand off to the async helper as IntPtrs (managed pointers can't cross an await).
        return WaitAndEncodeAsync(tcs.Task, (IntPtr)devicePtr, (IntPtr)contextPtr, framePool, session, path,
            hwnd, promoted, origExStyle, origOwner);
    }

    private static async Task WaitAndEncodeAsync(
        Task<Direct3D11CaptureFrame> frameTask,
        IntPtr deviceAbi,
        IntPtr contextAbi,
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session,
        string path,
        IntPtr hwnd,
        bool promoted,
        long origExStyle,
        long origOwner)
    {
        try
        {
            var winner = await Task.WhenAny(frameTask, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (winner != frameTask)
                throw new TimeoutException("WGC FrameArrived didn't fire within 10s; window may be occluded or DWM isn't compositing it.");
            using var frame = await frameTask.ConfigureAwait(false);
            EncodeFrameToPng(frame, deviceAbi, contextAbi, path);
        }
        finally
        {
            session.Dispose();
            framePool.Dispose();
            ReleasePointer(contextAbi);
            ReleasePointer(deviceAbi);
            if (promoted)
            {
                SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, origOwner);
                SetWindowLongPtrW(hwnd, GWL_EXSTYLE, origExStyle);
            }
        }
    }

    private static unsafe void ReleasePointer(IntPtr abi)
    {
        if (abi != IntPtr.Zero) ((IUnknown*)abi)->Release();
    }


    private static unsafe void EncodeFrameToPng(
        Direct3D11CaptureFrame frame,
        IntPtr deviceAbi,
        IntPtr contextAbi,
        string path)
    {
        var device = (ID3D11Device*)deviceAbi;
        var context = (ID3D11DeviceContext*)contextAbi;

        // Reach the underlying ID3D11Texture2D via IDirect3DDxgiInterfaceAccess (CsWinRT's .As<T>
        // performs the QI through the projection's RCW).
        var dxgiAccess = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var texIid = ID3D11Texture2D.Guid;
        Marshal.ThrowExceptionForHR(dxgiAccess.GetInterface(ref texIid, out var srcTexUnk));
        ID3D11Texture2D* srcTex = (ID3D11Texture2D*)srcTexUnk;
        try
        {
            Texture2DDesc desc;
            srcTex->GetDesc(&desc);

            // CPU-readable staging copy so we can map and read pixels.
            var stagingDesc = desc;
            stagingDesc.Usage = Usage.Staging;
            stagingDesc.CPUAccessFlags = (uint)CpuAccessFlag.Read;
            stagingDesc.BindFlags = 0;
            stagingDesc.MiscFlags = 0;
            ID3D11Texture2D* staging = null;
            SilkMarshal.ThrowHResult(device->CreateTexture2D(in stagingDesc, null, ref staging));
            try
            {
                context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)srcTex);

                MappedSubresource mapped = default;
                SilkMarshal.ThrowHResult(context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, ref mapped));
                try
                {
                    int w = (int)desc.Width;
                    int h = (int)desc.Height;
                    using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    var rect = new Rectangle(0, 0, w, h);
                    var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int srcPitch = (int)mapped.RowPitch;
                        int dstPitch = bd.Stride;
                        int rowBytes = w * 4;
                        var src = (byte*)mapped.PData;
                        var dst = (byte*)bd.Scan0;
                        for (int y = 0; y < h; y++)
                            Buffer.MemoryCopy(src + y * srcPitch, dst + y * dstPitch, dstPitch, rowBytes);
                    }
                    finally { bmp.UnlockBits(bd); }
                    bmp.Save(path, ImageFormat.Png);
                }
                finally { context->Unmap((ID3D11Resource*)staging, 0); }
            }
            finally { staging->Release(); }
        }
        finally { srcTex->Release(); }
    }
}
