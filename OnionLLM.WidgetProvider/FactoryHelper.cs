using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace OnionLLM.WidgetProvider.COM;

internal static class Guids
{
    public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
}

[ComImport]
[ComVisible(false)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid(Guids.IClassFactory)]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr objectPointer);

    [PreserveSig]
    int LockServer(bool shouldLock);
}

[ComVisible(true)]
internal sealed class WidgetProviderFactory<T> : IClassFactory where T : IWidgetProvider, new()
{
    public int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr objectPointer)
    {
        objectPointer = IntPtr.Zero;
        if (outer != IntPtr.Zero)
        {
            return -2147221232;
        }

        var providerPointer = IntPtr.Zero;
        try
        {
            providerPointer = MarshalInspectable<IWidgetProvider>.FromManaged(new T());
            return Marshal.QueryInterface(providerPointer, in interfaceId, out objectPointer);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(exception.ToString());
            return Marshal.GetHRForException(exception);
        }
        finally
        {
            if (providerPointer != IntPtr.Zero)
            {
                Marshal.Release(providerPointer);
            }
        }
    }

    int IClassFactory.LockServer(bool shouldLock) => 0;
}
