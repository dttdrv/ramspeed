using System.Runtime.InteropServices;

namespace optiRAM.Native;

internal static partial class NativeMethods
{
    // ── Process access rights ──
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;

    // ── Token access rights ──
    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint TOKEN_QUERY = 0x0008;

    // ── Privilege constants ──
    internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    // ── Privilege names ──
    internal const string SE_DEBUG_NAME = "SeDebugPrivilege";
    internal const string SE_PROFILE_SINGLE_PROCESS_NAME = "SeProfileSingleProcessPrivilege";
    internal const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";

    // ── NtSetSystemInformation classes ──
    internal const int SystemMemoryListInformation = 80;
    internal const int SystemCombinePhysicalMemoryInformation = 0x82; // 130 — page deduplication
    internal const int SystemRegistryReconciliationInformation = 0x9B; // 155 — flush registry cache

    // ── SYSTEM_MEMORY_LIST_COMMAND enum values ──
    internal enum MemoryListCommand
    {
        MemoryCaptureAccessedBits = 0,
        MemoryCaptureAndResetAccessedBits = 1,
        MemoryEmptyWorkingSets = 2,
        MemoryFlushModifiedList = 3,
        MemoryPurgeStandbyList = 4,
        MemoryPurgeLowPriorityStandbyList = 5,
        MemoryCommandMax = 6
    }

    // ── Process information classes ──
    internal const int ProcessMemoryPriority = 0x27;
    internal const int ProcessIoPriority = 0x1D; // 29 decimal — PROCESSINFOCLASS for I/O priority

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    internal const uint MEMORY_PRIORITY_VERY_LOW = 1;
    internal const uint MEMORY_PRIORITY_LOW = 2;
    internal const uint MEMORY_PRIORITY_MEDIUM = 3;
    internal const uint MEMORY_PRIORITY_BELOW_NORMAL = 4;
    internal const uint MEMORY_PRIORITY_NORMAL = 5;

    // ── Process power throttling (EcoQoS) ──
    internal const int ProcessPowerThrottling = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;

    // ── Structures ──

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    // ── kernel32.dll ──

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetSystemFileCacheSize(IntPtr MinimumFileCacheSize, IntPtr MaximumFileCacheSize, int Flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(
        IntPtr hProcess, int ProcessInformationClass,
        IntPtr ProcessInformation, uint ProcessInformationSize);

    // ── user32.dll (single-instance window activation) ──

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const int SW_RESTORE = 9;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    // ── user32.dll (foreground process detection) ──

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    /// <summary>Get the PID of the foreground window's process. Returns 0 on failure.</summary>
    internal static int GetForegroundProcessId()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    // ── psapi.dll / kernel32.dll (K32EmptyWorkingSet) ──

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(IntPtr hProcess);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    // ── ntdll.dll ──

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetSystemInformation(int SystemInformationClass, ref int SystemInformation, int SystemInformationLength);

    // Overload for null-buffer calls (e.g., registry reconciliation)
    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationNull(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    // Overload for memory combining (takes struct buffer)
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_COMBINE_INFORMATION_EX
    {
        public IntPtr Handle;         // in: 0
        public UIntPtr PagesCombined; // out: number of pages combined
    }

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationCombine(
        int SystemInformationClass,
        ref MEMORY_COMBINE_INFORMATION_EX SystemInformation,
        int SystemInformationLength);

    // ── kernel32.dll — Thread information ──

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadInformation(
        IntPtr hThread, int ThreadInformationClass,
        IntPtr ThreadInformation, uint ThreadInformationSize);

    internal const int ThreadMemoryPriority = 0x0001;
    internal const int ThreadPowerThrottling = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct THREAD_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    // ── kernel32.dll — Memory resource notifications ──

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr CreateMemoryResourceNotification(int NotificationType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryMemoryResourceNotification(
        IntPtr ResourceNotificationHandle, [MarshalAs(UnmanagedType.Bool)] out bool ResourceState);

    internal const int LowMemoryResourceNotification = 0;
    internal const int HighMemoryResourceNotification = 1;

    // ── kernel32.dll — Working set size limits ──

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint Flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessWorkingSetSizeEx(
        IntPtr hProcess, out IntPtr lpMinimumWorkingSetSize, out IntPtr lpMaximumWorkingSetSize, out uint Flags);

    internal const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x00000002;
    internal const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE  = 0x00000001;
    internal const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x00000008;
    internal const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE  = 0x00000004;

    // ── kernel32.dll — System file cache size ──

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemFileCacheSize(
        out IntPtr lpMinimumFileCacheSize, out IntPtr lpMaximumFileCacheSize, out int lpFlags);

    internal const int FILE_CACHE_MAX_HARD_ENABLE  = 0x00000001;
    internal const int FILE_CACHE_MAX_HARD_DISABLE = 0x00000002;
    internal const int FILE_CACHE_MIN_HARD_ENABLE  = 0x00000004;
    internal const int FILE_CACHE_MIN_HARD_DISABLE = 0x00000008;

    // ── kernel32.dll — Virtual memory diagnostics ──

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial UIntPtr VirtualQueryEx(
        IntPtr hProcess, IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    internal const uint MEM_COMMIT  = 0x1000;
    internal const uint MEM_RESERVE = 0x2000;
    internal const uint MEM_FREE    = 0x10000;
    internal const uint MEM_PRIVATE = 0x20000;
    internal const uint MEM_MAPPED  = 0x40000;
    internal const uint MEM_IMAGE   = 0x1000000;

    // ── kernel32.dll — Timer resolution optimization ──

    internal const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x00000004;

    // ── advapi32.dll ──

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValueW(string? lpSystemName, string lpName, ref LUID lpLuid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    // ── Self-optimization helpers ──

    internal static unsafe void SetSelfMemoryPriority(uint priority)
    {
        var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
        var ptr = (IntPtr)(&info);
        SetProcessInformation(GetCurrentProcess(), ProcessMemoryPriority,
            ptr, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
    }

    internal static unsafe void SetSelfEcoQoS()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
        };
        var ptr = (IntPtr)(&state);
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
            ptr, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    /// <summary>Set IGNORE_TIMER_RESOLUTION on self (Windows 11+). Silently no-ops on older OS.</summary>
    internal static unsafe void SetSelfIgnoreTimerResolution()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
        };
        var ptr = (IntPtr)(&state);
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
            ptr, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    /// <summary>Set current thread to specified memory priority.</summary>
    internal static unsafe void SetCurrentThreadMemoryPriority(uint priority)
    {
        var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
        var ptr = (IntPtr)(&info);
        SetThreadInformation(GetCurrentThread(), ThreadMemoryPriority,
            ptr, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
    }

    /// <summary>Set current thread to EcoQoS (efficiency mode).</summary>
    internal static unsafe void SetCurrentThreadEcoQoS()
    {
        var state = new THREAD_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
        };
        var ptr = (IntPtr)(&state);
        SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling,
            ptr, (uint)Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>());
    }

    /// <summary>Cap own working set with a hard maximum.</summary>
    internal static void SetSelfWorkingSetCap(long maxBytes)
    {
        var minSize = new IntPtr(1024 * 1024); // 1 MB minimum
        var maxSize = new IntPtr(maxBytes);
        SetProcessWorkingSetSizeEx(GetCurrentProcess(), minSize, maxSize,
            QUOTA_LIMITS_HARDWS_MIN_DISABLE | QUOTA_LIMITS_HARDWS_MAX_ENABLE);
    }

    /// <summary>Set memory priority on another process handle.</summary>
    internal static unsafe bool SetProcessMemoryPriority(IntPtr hProcess, uint priority)
    {
        var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
        var ptr = (IntPtr)(&info);
        return SetProcessInformation(hProcess, ProcessMemoryPriority,
            ptr, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
    }

    /// <summary>Set I/O priority on another process handle.</summary>
    internal static unsafe bool SetProcessIoPriority(IntPtr hProcess, int ioPriority)
    {
        int* ptr = &ioPriority;
        return SetProcessInformation(hProcess, ProcessIoPriority,
            (IntPtr)ptr, sizeof(int));
    }

    internal const int IO_PRIORITY_VERY_LOW = 0;
    internal const int IO_PRIORITY_LOW = 1;
    internal const int IO_PRIORITY_NORMAL = 2;
}
