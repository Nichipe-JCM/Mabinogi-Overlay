using System.ComponentModel;
using System.Runtime.InteropServices;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

internal static class ProcessPrivilegeInspector
{
    public static string DescribeWindowElevation(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return "unknown(no-window)";
        }

        Win32Methods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return DescribeError("GetWindowThreadProcessId", Marshal.GetLastPInvokeError());
        }

        Marshal.SetLastPInvokeError(0);
        var processHandle = Win32Methods.OpenProcess(
            Win32Methods.ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (processHandle == nint.Zero)
        {
            return DescribeError("OpenProcess", Marshal.GetLastPInvokeError());
        }

        try
        {
            Marshal.SetLastPInvokeError(0);
            if (!Win32Methods.OpenProcessToken(
                    processHandle,
                    Win32Methods.TokenQuery,
                    out var tokenHandle))
            {
                return DescribeError("OpenProcessToken", Marshal.GetLastPInvokeError());
            }

            try
            {
                Marshal.SetLastPInvokeError(0);
                if (!Win32Methods.GetTokenInformation(
                        tokenHandle,
                        Win32Methods.TokenElevationInformationClass,
                        out var elevation,
                        Marshal.SizeOf<Win32Methods.TokenElevationNative>(),
                        out _))
                {
                    return DescribeError("GetTokenInformation", Marshal.GetLastPInvokeError());
                }

                return elevation.TokenIsElevated != 0 ? "true" : "false";
            }
            finally
            {
                Win32Methods.CloseHandle(tokenHandle);
            }
        }
        finally
        {
            Win32Methods.CloseHandle(processHandle);
        }
    }

    private static string DescribeError(string operation, int error)
    {
        var message = error == 0 ? "unknown" : new Win32Exception(error).Message;
        return $"unknown({operation},error={error}:{message})";
    }
}
