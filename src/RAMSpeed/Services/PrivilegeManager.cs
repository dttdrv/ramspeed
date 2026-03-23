using RAMSpeed.Native;

namespace RAMSpeed.Services;

internal static class PrivilegeManager
{
    public static bool EnablePrivilege(string privilegeName)
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                out var tokenHandle))
            return false;

        try
        {
            var luid = new NativeMethods.LUID();
            if (!NativeMethods.LookupPrivilegeValueW(null, privilegeName, ref luid))
                return false;

            var tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                }
            };

            return NativeMethods.AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.CloseHandle(tokenHandle);
        }
    }

    public static void EnableAllRequired()
    {
        EnablePrivilege(NativeMethods.SE_DEBUG_NAME);
        EnablePrivilege(NativeMethods.SE_PROFILE_SINGLE_PROCESS_NAME);
        EnablePrivilege(NativeMethods.SE_INCREASE_QUOTA_NAME);
    }
}
