using System.Runtime.InteropServices;
using OnionLLM.WidgetProvider;
using OnionLLM.WidgetProvider.COM;

[DllImport("ole32.dll")]
static extern int CoRegisterClassObject(
    [MarshalAs(UnmanagedType.LPStruct)] Guid classId,
    [MarshalAs(UnmanagedType.IUnknown)] object factory,
    uint classContext,
    uint flags,
    out uint registrationCookie);

[DllImport("ole32.dll")]
static extern int CoRevokeClassObject(uint registrationCookie);

WinRT.ComWrappersSupport.InitializeComWrappers();
var classId = Guid.Parse("6F56F2D0-E2D8-48E0-A0A3-7E6E6B6A4C22");
var factory = new WidgetProviderFactory<OnionLLM.WidgetProvider.WidgetProvider>();
Marshal.ThrowExceptionForHR(CoRegisterClassObject(classId, factory, 0x4, 0x1, out var cookie));

try
{
    OnionLLM.WidgetProvider.WidgetProvider.GetEmptyWidgetListEvent().WaitOne();
}
finally
{
    Marshal.ThrowExceptionForHR(CoRevokeClassObject(cookie));
    GC.KeepAlive(factory);
}
