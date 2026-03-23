use std::collections::HashSet;
use std::mem;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

use windows::Win32::Foundation::CloseHandle;
use windows::Win32::System::Memory::{SetProcessWorkingSetSizeEx, SETPROCESSWORKINGSETSIZEEX_FLAGS};
use windows::Win32::System::ProcessStatus::K32EmptyWorkingSet;
use windows::Win32::System::Threading::{
    GetCurrentProcess, OpenProcess, PROCESS_QUERY_INFORMATION, PROCESS_SET_INFORMATION,
    PROCESS_SET_QUOTA,
};

use crate::memory_info::{
    MemoryListCommand, get_memory_info, set_memory_list_command,
};
use crate::settings::OptimizationLevel;

/// Prevents concurrent optimization runs.
static OPTIMIZING: AtomicBool = AtomicBool::new(false);

#[derive(Debug, Clone)]
pub struct OptimizationResult {
    pub timestamp: Instant,
    pub memory_before_mb: f64,
    pub memory_after_mb: f64,
    pub memory_freed_bytes: i64,
    pub processes_trimmed: u32,
    pub duration_ms: f64,
    pub methods_used: Vec<&'static str>,
    pub success: bool,
    pub error_message: Option<String>,
}

impl OptimizationResult {
    pub fn freed_mb(&self) -> f64 {
        self.memory_freed_bytes.max(0) as f64 / (1024.0 * 1024.0)
    }

    pub fn summary(&self) -> String {
        if self.success {
            format!(
                "Freed {:.1} MB in {:.0}ms ({} processes trimmed)",
                self.freed_mb(),
                self.duration_ms,
                self.processes_trimmed
            )
        } else {
            format!(
                "Failed: {}",
                self.error_message.as_deref().unwrap_or("unknown error")
            )
        }
    }
}

/// Default system process exclusion list.
pub fn default_exclusions() -> HashSet<String> {
    [
        "System", "Idle", "smss", "csrss", "wininit", "services", "lsass",
        "svchost", "dwm", "winlogon", "Memory Compression", "Registry",
        "fontdrvhost", "conhost",
    ]
    .iter()
    .map(|s| s.to_lowercase())
    .collect()
}

/// Trim working sets of all non-excluded processes.
pub fn trim_process_working_sets(excluded: &HashSet<String>) -> (u32, u32, u32) {
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
        TH32CS_SNAPPROCESS,
    };

    let mut trimmed = 0u32;
    let mut failed = 0u32;
    let mut skipped = 0u32;

    unsafe {
        let snapshot = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
            Ok(h) => h,
            Err(_) => return (0, 0, 0),
        };

        let mut entry = PROCESSENTRY32W {
            dwSize: mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };

        if Process32FirstW(snapshot, &mut entry).is_err() {
            let _ = CloseHandle(snapshot);
            return (0, 0, 0);
        }

        loop {
            let name_raw = &entry.szExeFile[..entry
                .szExeFile
                .iter()
                .position(|&c| c == 0)
                .unwrap_or(entry.szExeFile.len())];
            let name = String::from_utf16_lossy(name_raw);
            // Strip .exe extension for matching
            let base_name = name.strip_suffix(".exe").unwrap_or(&name);

            if excluded.contains(&base_name.to_lowercase()) {
                skipped += 1;
            } else {
                let handle = OpenProcess(
                    PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA,
                    false,
                    entry.th32ProcessID,
                );
                match handle {
                    Ok(h) => {
                        if K32EmptyWorkingSet(h).as_bool() {
                            trimmed += 1;
                        } else {
                            failed += 1;
                        }
                        let _ = CloseHandle(h);
                    }
                    Err(_) => failed += 1,
                }
            }

            if Process32NextW(snapshot, &mut entry).is_err() {
                break;
            }
        }

        let _ = CloseHandle(snapshot);
    }

    (trimmed, failed, skipped)
}

pub fn purge_standby_list() -> bool {
    set_memory_list_command(MemoryListCommand::PurgeStandbyList)
}

pub fn purge_low_priority_standby() -> bool {
    set_memory_list_command(MemoryListCommand::PurgeLowPriorityStandbyList)
}

pub fn flush_modified_list() -> bool {
    set_memory_list_command(MemoryListCommand::FlushModifiedList)
}

pub fn capture_and_reset_accessed_bits() -> bool {
    set_memory_list_command(MemoryListCommand::CaptureAndResetAccessedBits)
}

pub fn empty_system_working_sets() -> bool {
    set_memory_list_command(MemoryListCommand::EmptyWorkingSets)
}

pub fn flush_system_file_cache() -> bool {
    unsafe {
        windows::Win32::System::Memory::SetSystemFileCacheSize(usize::MAX, usize::MAX, 0).is_ok()
    }
}

/// Set a hard max file cache size. Pass 0 to clear the limit.
pub fn set_file_cache_hard_max(max_bytes: u64) -> bool {
    unsafe {
        if max_bytes == 0 {
            windows::Win32::System::Memory::SetSystemFileCacheSize(0, 0, 0x2 /* DISABLE */)
                .is_ok()
        } else {
            windows::Win32::System::Memory::SetSystemFileCacheSize(
                0,
                max_bytes as usize,
                0x1, /* ENABLE */
            )
            .is_ok()
        }
    }
}

/// Set a hard working set cap on a process.
pub fn set_process_working_set_cap(pid: u32, max_bytes: u64) -> bool {
    unsafe {
        let handle = match OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA,
            false,
            pid,
        ) {
            Ok(h) => h,
            Err(_) => return false,
        };
        let min_size = 1024 * 1024usize; // 1 MB
        let result = SetProcessWorkingSetSizeEx(
            handle,
            min_size,
            max_bytes as usize,
            SETPROCESSWORKINGSETSIZEEX_FLAGS(0x2 | 0x4), // HARDWS_MIN_DISABLE | HARDWS_MAX_ENABLE
        );
        let _ = CloseHandle(handle);
        result.is_ok()
    }
}

/// Set memory priority on a process.
pub fn set_process_memory_priority(pid: u32, priority: u32) -> bool {
    unsafe {
        let handle = match OpenProcess(PROCESS_SET_INFORMATION, false, pid) {
            Ok(h) => h,
            Err(_) => return false,
        };

        #[repr(C)]
        struct MemPrioInfo {
            priority: u32,
        }
        let mut info = MemPrioInfo { priority };
        let result = windows::Win32::System::Threading::SetProcessInformation(
            handle,
            windows::Win32::System::Threading::ProcessMemoryPriority,
            &mut info as *mut _ as *const std::ffi::c_void,
            mem::size_of::<MemPrioInfo>() as u32,
        );
        let _ = CloseHandle(handle);
        result.is_ok()
    }
}

/// Trim our own working set (no GC needed in Rust!).
pub fn trim_self(cap_mb: u32) {
    unsafe {
        let proc = GetCurrentProcess();
        let _ = K32EmptyWorkingSet(proc);
        if cap_mb > 0 {
            let max_bytes = cap_mb as usize * 1024 * 1024;
            let _ = SetProcessWorkingSetSizeEx(
                proc,
                1024 * 1024, // 1 MB min
                max_bytes,
                SETPROCESSWORKINGSETSIZEEX_FLAGS(0x2 | 0x4), // HARDWS_MIN_DISABLE | HARDWS_MAX_ENABLE
            );
        }
    }
}

/// Set self process to low memory priority.
pub fn set_self_low_priority() {
    unsafe {
        let proc = GetCurrentProcess();

        // Memory priority = LOW (2)
        #[repr(C)]
        struct MemPrioInfo {
            priority: u32,
        }
        let mut info = MemPrioInfo { priority: 2 };
        let _ = windows::Win32::System::Threading::SetProcessInformation(
            proc,
            windows::Win32::System::Threading::ProcessMemoryPriority,
            &mut info as *mut _ as *const std::ffi::c_void,
            mem::size_of::<MemPrioInfo>() as u32,
        );

        // EcoQoS (power throttling)
        #[repr(C)]
        struct PowerThrottle {
            version: u32,
            control_mask: u32,
            state_mask: u32,
        }
        let mut pt = PowerThrottle {
            version: 1,
            control_mask: 1, // EXECUTION_SPEED
            state_mask: 1,
        };
        let _ = windows::Win32::System::Threading::SetProcessInformation(
            proc,
            windows::Win32::System::Threading::ProcessPowerThrottling,
            &mut pt as *mut _ as *const std::ffi::c_void,
            mem::size_of::<PowerThrottle>() as u32,
        );

        // Ignore timer resolution (Windows 11+)
        let mut pt2 = PowerThrottle {
            version: 1,
            control_mask: 0x4, // IGNORE_TIMER_RESOLUTION
            state_mask: 0x4,
        };
        let _ = windows::Win32::System::Threading::SetProcessInformation(
            proc,
            windows::Win32::System::Threading::ProcessPowerThrottling,
            &mut pt2 as *mut _ as *const std::ffi::c_void,
            mem::size_of::<PowerThrottle>() as u32,
        );
    }
}

/// Run full optimization based on level. Thread-safe — only one run at a time.
pub fn optimize_all(
    level: OptimizationLevel,
    cache_max_percent: u32,
    excluded: &HashSet<String>,
) -> OptimizationResult {
    // Prevent concurrent runs
    if OPTIMIZING
        .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
        .is_err()
    {
        return OptimizationResult {
            timestamp: Instant::now(),
            memory_before_mb: 0.0,
            memory_after_mb: 0.0,
            memory_freed_bytes: 0,
            processes_trimmed: 0,
            duration_ms: 0.0,
            methods_used: vec![],
            success: false,
            error_message: Some("Optimization already in progress".into()),
        };
    }

    let start = Instant::now();
    let mut methods_used = Vec::new();
    let before = get_memory_info();
    let before_avail = before.available_physical_bytes;

    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        // Step 1: Trim process working sets (all levels)
        let (trimmed, _, _) = trim_process_working_sets(excluded);
        methods_used.push("Working Set Trim");

        if level >= OptimizationLevel::Balanced {
            // Step 2: Flush modified page list
            if flush_modified_list() {
                methods_used.push("Modified List Flush");
            }

            // Step 3: Reset accessed bits
            if capture_and_reset_accessed_bits() {
                methods_used.push("Access Bits Reset");
            }

            // Step 4: Purge standby list
            if level == OptimizationLevel::Balanced {
                if purge_low_priority_standby() {
                    methods_used.push("Low-Priority Standby Purge");
                }
            } else {
                // Aggressive
                if empty_system_working_sets() {
                    methods_used.push("System Working Set Empty");
                }
                if purge_standby_list() {
                    methods_used.push("Standby List Purge");
                }
            }

            // Step 5: Flush system file cache
            if flush_system_file_cache() {
                methods_used.push("File Cache Flush");
            }

            // Step 6: Apply hard file cache max if configured
            if cache_max_percent > 0 {
                let max_bytes =
                    (before.total_physical_bytes as f64 * cache_max_percent as f64 / 100.0) as u64;
                if set_file_cache_hard_max(max_bytes) {
                    methods_used.push("Cache Cap");
                }
            }
        }

        trimmed
    }));

    OPTIMIZING.store(false, Ordering::SeqCst);

    match result {
        Ok(trimmed) => {
            let duration = start.elapsed();
            let after = get_memory_info();
            let freed = after.available_physical_bytes as i64 - before_avail as i64;

            OptimizationResult {
                timestamp: Instant::now(),
                memory_before_mb: before_avail as f64 / (1024.0 * 1024.0),
                memory_after_mb: after.available_physical_bytes as f64 / (1024.0 * 1024.0),
                memory_freed_bytes: freed.max(0),
                processes_trimmed: trimmed,
                duration_ms: duration.as_secs_f64() * 1000.0,
                methods_used,
                success: true,
                error_message: None,
            }
        }
        Err(e) => {
            let duration = start.elapsed();
            OptimizationResult {
                timestamp: Instant::now(),
                memory_before_mb: 0.0,
                memory_after_mb: 0.0,
                memory_freed_bytes: 0,
                processes_trimmed: 0,
                duration_ms: duration.as_secs_f64() * 1000.0,
                methods_used,
                success: false,
                error_message: Some(format!("{e:?}")),
            }
        }
    }
}
