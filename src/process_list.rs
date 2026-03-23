use std::collections::HashSet;
use std::mem;

/// Information about a running process.
#[derive(Debug, Clone)]
pub struct ProcessInfo {
    pub pid: u32,
    pub name: String,
    pub working_set_bytes: u64,
    pub private_bytes: u64,
    pub is_excluded: bool,
}

impl ProcessInfo {
    pub fn working_set_mb(&self) -> f64 {
        self.working_set_bytes as f64 / (1024.0 * 1024.0)
    }
    pub fn private_mb(&self) -> f64 {
        self.private_bytes as f64 / (1024.0 * 1024.0)
    }
}

/// Enumerate top processes by working set.
pub fn get_top_processes(excluded: &HashSet<String>, max_count: usize) -> Vec<ProcessInfo> {
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
        TH32CS_SNAPPROCESS,
    };
    use windows::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
    use windows::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};

    let mut processes = Vec::new();

    unsafe {
        let snapshot = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
            Ok(h) => h,
            Err(_) => return processes,
        };

        let mut entry = PROCESSENTRY32W {
            dwSize: mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };

        if Process32FirstW(snapshot, &mut entry).is_err() {
            let _ = CloseHandle(snapshot);
            return processes;
        }

        loop {
            let name_raw = &entry.szExeFile[..entry
                .szExeFile
                .iter()
                .position(|&c| c == 0)
                .unwrap_or(entry.szExeFile.len())];
            let name = String::from_utf16_lossy(name_raw);
            let base_name = name.strip_suffix(".exe").unwrap_or(&name).to_string();

            if entry.th32ProcessID != 0 {
                let mut ws_bytes = 0u64;
                let mut priv_bytes = 0u64;

                if let Ok(proc) = OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    entry.th32ProcessID,
                ) {
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
                        ws_bytes = counters.WorkingSetSize as u64;
                        // PagefileUsage approximates "private bytes"
                        priv_bytes = counters.PagefileUsage as u64;
                    }
                    let _ = CloseHandle(proc);
                }

                processes.push(ProcessInfo {
                    pid: entry.th32ProcessID,
                    name: base_name.clone(),
                    working_set_bytes: ws_bytes,
                    private_bytes: priv_bytes,
                    is_excluded: excluded.contains(&base_name.to_lowercase()),
                });
            }

            if Process32NextW(snapshot, &mut entry).is_err() {
                break;
            }
        }

        let _ = CloseHandle(snapshot);
    }

    // Sort by working set descending and take top N
    processes.sort_by(|a, b| b.working_set_bytes.cmp(&a.working_set_bytes));
    processes.truncate(max_count);
    processes
}
