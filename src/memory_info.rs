#[derive(Debug, Clone, Default)]
pub struct MemoryInfo {
    pub total_physical_bytes: u64,
    pub available_physical_bytes: u64,
    pub cached_bytes: u64,
    pub standby_bytes: u64,
    pub free_bytes: u64,
    pub modified_bytes: u64,
    pub total_page_file_bytes: u64,
    pub available_page_file_bytes: u64,
    pub page_size: u64,
    pub process_count: u32,
    pub thread_count: u32,
    pub handle_count: u32,
    pub kernel_total_bytes: u64,
    pub kernel_paged_bytes: u64,
    pub kernel_nonpaged_bytes: u64,
    pub commit_total_bytes: u64,
    pub commit_limit_bytes: u64,
    pub compressed_bytes: u64,
}

impl MemoryInfo {
    pub fn used_physical_bytes(&self) -> u64 {
        self.total_physical_bytes.saturating_sub(self.available_physical_bytes)
    }

    pub fn usage_percent(&self) -> f64 {
        if self.total_physical_bytes == 0 {
            return 0.0;
        }
        self.used_physical_bytes() as f64 / self.total_physical_bytes as f64 * 100.0
    }

    pub fn total_gb(&self) -> f64 {
        self.total_physical_bytes as f64 / (1024.0 * 1024.0 * 1024.0)
    }
    pub fn used_gb(&self) -> f64 {
        self.used_physical_bytes() as f64 / (1024.0 * 1024.0 * 1024.0)
    }
    pub fn available_gb(&self) -> f64 {
        self.available_physical_bytes as f64 / (1024.0 * 1024.0 * 1024.0)
    }
    pub fn cached_gb(&self) -> f64 {
        self.cached_bytes as f64 / (1024.0 * 1024.0 * 1024.0)
    }
    pub fn modified_gb(&self) -> f64 {
        self.modified_bytes as f64 / (1024.0 * 1024.0 * 1024.0)
    }
    pub fn standby_gb(&self) -> f64 {
        self.standby_bytes as f64 / (1024.0 * 1024.0 * 1024.0)
    }
    pub fn free_gb(&self) -> f64 {
        self.free_bytes as f64 / (1024.0 * 1024.0 * 1024.0)
    }
    pub fn compressed_mb(&self) -> f64 {
        self.compressed_bytes as f64 / (1024.0 * 1024.0)
    }
    pub fn kernel_paged_mb(&self) -> f64 {
        self.kernel_paged_bytes as f64 / (1024.0 * 1024.0)
    }
    pub fn kernel_nonpaged_mb(&self) -> f64 {
        self.kernel_nonpaged_bytes as f64 / (1024.0 * 1024.0)
    }
    pub fn commit_percent(&self) -> f64 {
        if self.commit_limit_bytes == 0 {
            return 0.0;
        }
        self.commit_total_bytes as f64 / self.commit_limit_bytes as f64 * 100.0
    }
}

use std::mem;
use windows::Win32::System::ProcessStatus::{
    K32GetPerformanceInfo, PERFORMANCE_INFORMATION,
};
use windows::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};

// NtSetSystemInformation and NtQuerySystemInformation — undocumented ntdll APIs
#[link(name = "ntdll")]
unsafe extern "system" {
    fn NtSetSystemInformation(
        class: u32,
        info: *mut std::ffi::c_void,
        length: u32,
    ) -> i32;
    fn NtQuerySystemInformation(
        class: u32,
        info: *mut std::ffi::c_void,
        length: u32,
        ret_length: *mut u32,
    ) -> i32;
}

pub const SYSTEM_MEMORY_LIST_INFORMATION: u32 = 80;

/// Memory list info returned by NtQuerySystemInformation(SystemMemoryListInformation).
/// Based on the SYSTEM_MEMORY_LIST_INFORMATION structure.
#[repr(C)]
#[derive(Debug, Default)]
struct SystemMemoryListInformation {
    zero_page_count: usize,
    free_page_count: usize,
    modified_page_count: usize,
    modified_no_write_page_count: usize,
    bad_page_count: usize,
    page_count_by_priority: [usize; 8], // standby pages by priority 0..7
    repurposed_page_count_by_priority: [usize; 8],
    modified_page_count_page_file: usize,
}

/// Memory list commands for NtSetSystemInformation(SystemMemoryListInformation).
#[repr(i32)]
#[derive(Debug, Clone, Copy)]
pub enum MemoryListCommand {
    CaptureAccessedBits = 0,
    CaptureAndResetAccessedBits = 1,
    EmptyWorkingSets = 2,
    FlushModifiedList = 3,
    PurgeStandbyList = 4,
    PurgeLowPriorityStandbyList = 5,
}

/// Execute a memory list command via NtSetSystemInformation.
pub fn set_memory_list_command(cmd: MemoryListCommand) -> bool {
    unsafe {
        let mut command = cmd as i32;
        let status = NtSetSystemInformation(
            SYSTEM_MEMORY_LIST_INFORMATION,
            &mut command as *mut _ as *mut std::ffi::c_void,
            mem::size_of::<i32>() as u32,
        );
        status >= 0 // NT_SUCCESS
    }
}

/// Query current system memory info.
pub fn get_memory_info() -> MemoryInfo {
    let mut info = MemoryInfo::default();

    // GlobalMemoryStatusEx
    let mut mem_status = MEMORYSTATUSEX {
        dwLength: mem::size_of::<MEMORYSTATUSEX>() as u32,
        ..Default::default()
    };
    unsafe {
        let _ = GlobalMemoryStatusEx(&mut mem_status);
    }

    // GetPerformanceInfo
    let mut perf_info = PERFORMANCE_INFORMATION {
        cb: mem::size_of::<PERFORMANCE_INFORMATION>() as u32,
        ..Default::default()
    };
    unsafe {
        let _ = K32GetPerformanceInfo(
            &mut perf_info,
            mem::size_of::<PERFORMANCE_INFORMATION>() as u32,
        );
    }

    let page_size = perf_info.PageSize as u64;
    let phys_total = perf_info.PhysicalTotal as u64 * page_size;
    let _phys_avail = perf_info.PhysicalAvailable as u64 * page_size;
    let sys_cache = perf_info.SystemCache as u64 * page_size;

    info.total_physical_bytes = phys_total;
    info.available_physical_bytes = mem_status.ullAvailPhys;
    info.cached_bytes = sys_cache;
    info.total_page_file_bytes = mem_status.ullTotalPageFile;
    info.available_page_file_bytes = mem_status.ullAvailPageFile;
    info.page_size = page_size;
    info.process_count = perf_info.ProcessCount;
    info.thread_count = perf_info.ThreadCount;
    info.handle_count = perf_info.HandleCount;
    info.kernel_total_bytes = perf_info.KernelTotal as u64 * page_size;
    info.kernel_paged_bytes = perf_info.KernelPaged as u64 * page_size;
    info.kernel_nonpaged_bytes = perf_info.KernelNonpaged as u64 * page_size;
    info.commit_total_bytes = perf_info.CommitTotal as u64 * page_size;
    info.commit_limit_bytes = perf_info.CommitLimit as u64 * page_size;

    // Query standby/modified/free breakdown via NtQuerySystemInformation
    let mut list_info = SystemMemoryListInformation::default();
    let mut ret_len = 0u32;
    let status = unsafe {
        NtQuerySystemInformation(
            SYSTEM_MEMORY_LIST_INFORMATION,
            &mut list_info as *mut _ as *mut std::ffi::c_void,
            mem::size_of::<SystemMemoryListInformation>() as u32,
            &mut ret_len,
        )
    };

    if status >= 0 {
        // Standby = sum of all priority buckets
        let standby_pages: usize = list_info.page_count_by_priority.iter().sum();
        info.standby_bytes = standby_pages as u64 * page_size;
        info.modified_bytes = list_info.modified_page_count as u64 * page_size;
        info.free_bytes = (list_info.free_page_count + list_info.zero_page_count) as u64 * page_size;
    } else {
        // Fallback estimation
        info.standby_bytes = sys_cache.min(mem_status.ullAvailPhys);
        info.free_bytes = mem_status.ullAvailPhys.saturating_sub(info.standby_bytes);
    }

    // Compressed memory: read "Memory Compression" process working set
    info.compressed_bytes = get_compressed_memory_bytes();

    info
}

/// Read compressed memory size from the "Memory Compression" process.
fn get_compressed_memory_bytes() -> u64 {
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
        TH32CS_SNAPPROCESS,
    };
    use windows::Win32::System::ProcessStatus::K32GetProcessMemoryInfo;
    use windows::Win32::System::ProcessStatus::PROCESS_MEMORY_COUNTERS;
    use windows::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};

    unsafe {
        let snapshot = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
            Ok(h) => h,
            Err(_) => return 0,
        };

        let mut entry = PROCESSENTRY32W {
            dwSize: mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };

        if Process32FirstW(snapshot, &mut entry).is_err() {
            let _ = windows::Win32::Foundation::CloseHandle(snapshot);
            return 0;
        }

        loop {
            let name = String::from_utf16_lossy(
                &entry.szExeFile[..entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len())],
            );
            if name == "Memory Compression" || name == "MemCompression" {
                if let Ok(proc) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, entry.th32ProcessID) {
                    let mut counters = PROCESS_MEMORY_COUNTERS {
                        cb: mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
                        ..Default::default()
                    };
                    if K32GetProcessMemoryInfo(
                        proc,
                        &mut counters,
                        mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
                    )
                    .as_bool()
                    {
                        let _ = windows::Win32::Foundation::CloseHandle(proc);
                        let _ = windows::Win32::Foundation::CloseHandle(snapshot);
                        return counters.WorkingSetSize as u64;
                    }
                    let _ = windows::Win32::Foundation::CloseHandle(proc);
                }
            }

            if Process32NextW(snapshot, &mut entry).is_err() {
                break;
            }
        }

        let _ = windows::Win32::Foundation::CloseHandle(snapshot);
        0
    }
}

/// Query OS low-memory resource notification state.
pub fn is_low_memory(handle: windows::Win32::Foundation::HANDLE) -> bool {
    if handle.is_invalid() {
        return false;
    }
    unsafe {
        let mut state = windows::core::BOOL(0);
        if windows::Win32::System::Memory::QueryMemoryResourceNotification(handle, &mut state)
            .is_ok()
        {
            state.as_bool()
        } else {
            false
        }
    }
}

/// Create OS memory resource notification handles.
pub fn create_memory_notifications() -> (windows::Win32::Foundation::HANDLE, windows::Win32::Foundation::HANDLE) {
    use windows::Win32::System::Memory::{
        CreateMemoryResourceNotification, LowMemoryResourceNotification,
        HighMemoryResourceNotification,
    };
    unsafe {
        let low = CreateMemoryResourceNotification(LowMemoryResourceNotification)
            .unwrap_or(windows::Win32::Foundation::HANDLE::default());
        let high = CreateMemoryResourceNotification(HighMemoryResourceNotification)
            .unwrap_or(windows::Win32::Foundation::HANDLE::default());
        (low, high)
    }
}
