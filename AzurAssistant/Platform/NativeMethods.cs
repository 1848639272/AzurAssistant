using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Windows.Foundation;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace AzurAssistant.Platform;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    internal delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int GetCloaked(nint hwnd, int attribute, out int value, int size);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint levelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);

    internal static IDirect3DDevice CreateDevice()
    {
        nint native = 0, context = 0, dxgi = 0, projected = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native, out _, out context));
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            return MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        finally
        {
            if (projected != 0) Marshal.Release(projected);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (native != 0) Marshal.Release(native);
        }
    }

    internal static unsafe GraphicsCaptureItem CreateCaptureItem(nint hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint hstring = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out hstring));
            var interopId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in interopId, out factory));
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            Marshal.ThrowExceptionForHR(create(factory, hwnd, &itemId, &item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }

    // Query the Windows 11 capture interfaces at runtime while retaining the 19041 client baseline.
    internal static unsafe IAsyncOperation<AppCapabilityAccessStatus> RequestBorderlessAccess()
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureAccess";
        nint hstring = 0, factory = 0, operation = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out hstring));
            var iid = new Guid("743ed370-06ec-5040-a58a-901f0f757095");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in iid, out factory));
            var request = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)(*(nint**)factory)[6];
            Marshal.ThrowExceptionForHR(request(factory, 0, &operation)); // GraphicsCaptureAccessKind.Borderless
            return MarshalInterface<IAsyncOperation<AppCapabilityAccessStatus>>.FromAbi(operation);
        }
        finally
        {
            if (operation != 0) Marshal.Release(operation);
            if (factory != 0) Marshal.Release(factory);
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }

    internal static unsafe bool TryHideCaptureBorder(GraphicsCaptureSession session)
    {
        var iid = new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be");
        if (Marshal.QueryInterface(((IWinRTObject)session).NativeObject.ThisPtr, in iid, out var session3) < 0) return false;
        try
        {
            var set = (delegate* unmanaged[Stdcall]<nint, byte, int>)(*(nint**)session3)[7];
            return set(session3, 0) >= 0;
        }
        finally { Marshal.Release(session3); }
    }
}
