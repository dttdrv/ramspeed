use std::collections::HashSet;
use std::sync::mpsc;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use crate::memory_info::{self, MemoryInfo};
use crate::optimizer::{self, OptimizationResult};
use crate::settings::{OptimizationLevel, SettingsHandle};

/// Messages sent from the monitor thread to the UI.
#[derive(Debug, Clone)]
pub enum MonitorEvent {
    MemoryUpdated(MemoryInfo),
    OptimizationCompleted(OptimizationResult),
}

/// Commands sent from the UI to the monitor thread.
pub enum MonitorCommand {
    OptimizeNow,
    UpdateSettings,
    Stop,
}

/// Shared state between monitor and UI for settings that change at runtime.
pub struct MonitorConfig {
    pub auto_optimize_enabled: bool,
    pub threshold_percent: u32,
    pub cooldown_seconds: u32,
    pub check_interval_seconds: u32,
    pub level: OptimizationLevel,
    pub cache_max_percent: u32,
    pub self_working_set_cap_mb: u32,
    pub excluded_processes: HashSet<String>,
}

impl MonitorConfig {
    pub fn from_settings(settings: &crate::settings::Settings) -> Self {
        Self {
            auto_optimize_enabled: settings.auto_optimize_enabled,
            threshold_percent: settings.threshold_percent,
            cooldown_seconds: settings.cooldown_seconds,
            check_interval_seconds: settings.check_interval_seconds,
            level: settings.level,
            cache_max_percent: settings.cache_max_percent,
            self_working_set_cap_mb: settings.self_working_set_cap_mb,
            excluded_processes: settings
                .excluded_processes
                .iter()
                .map(|s| s.to_lowercase())
                .collect(),
        }
    }
}

/// Starts the background monitor thread.
/// Returns (event_receiver, command_sender).
pub fn start_monitor(
    settings_handle: SettingsHandle,
) -> (mpsc::Receiver<MonitorEvent>, mpsc::Sender<MonitorCommand>) {
    let (event_tx, event_rx) = mpsc::channel();
    let (cmd_tx, cmd_rx) = mpsc::channel();

    let config = Arc::new(Mutex::new(MonitorConfig::from_settings(
        &settings_handle.get(),
    )));
    let config_clone = Arc::clone(&config);
    let settings_for_thread = settings_handle.clone();

    thread::Builder::new()
        .name("ramspeed-monitor".into())
        .spawn(move || {
            monitor_loop(event_tx, cmd_rx, config_clone, settings_for_thread);
        })
        .expect("Failed to spawn monitor thread");

    // Store config for updating from UI side
    // The cmd_tx sender lets the UI push UpdateSettings to reload config
    // We need to expose config to the UI, so let's return the sender
    // and let the app store a reference to config separately.

    (event_rx, cmd_tx)
}

fn monitor_loop(
    event_tx: mpsc::Sender<MonitorEvent>,
    cmd_rx: mpsc::Receiver<MonitorCommand>,
    config: Arc<Mutex<MonitorConfig>>,
    settings_handle: SettingsHandle,
) {
    // Apply thread-level optimizations
    set_thread_low_priority();

    // Create OS memory resource notification handles
    let (low_mem_handle, _high_mem_handle) = memory_info::create_memory_notifications();

    let mut last_optimization = Instant::now() - Duration::from_secs(3600);
    let mut tick_count = 0u64;

    loop {
        // Read current interval
        let interval = {
            let cfg = config.lock().unwrap();
            Duration::from_secs(cfg.check_interval_seconds as u64)
        };

        // Sleep in small increments so we can respond to commands quickly
        let sleep_start = Instant::now();
        let mut should_stop = false;
        let mut force_optimize = false;

        while sleep_start.elapsed() < interval {
            match cmd_rx.try_recv() {
                Ok(MonitorCommand::Stop) => {
                    should_stop = true;
                    break;
                }
                Ok(MonitorCommand::OptimizeNow) => {
                    force_optimize = true;
                    break;
                }
                Ok(MonitorCommand::UpdateSettings) => {
                    let new_settings = settings_handle.get();
                    let mut cfg = config.lock().unwrap();
                    *cfg = MonitorConfig::from_settings(&new_settings);
                }
                Err(mpsc::TryRecvError::Empty) => {}
                Err(mpsc::TryRecvError::Disconnected) => {
                    should_stop = true;
                    break;
                }
            }
            thread::sleep(Duration::from_millis(100));
        }

        if should_stop {
            break;
        }

        // Query memory info
        let info = memory_info::get_memory_info();
        let _ = event_tx.send(MonitorEvent::MemoryUpdated(info.clone()));

        // Self-trim to keep RAMSpeed lean (cheap in Rust — just EmptyWorkingSet, no GC)
        let cap = config.lock().unwrap().self_working_set_cap_mb;
        // Only trim every 10 ticks to avoid excessive calls
        tick_count += 1;
        if tick_count % 10 == 0 {
            optimizer::trim_self(cap);
        }

        // Check for OS low-memory notification
        let is_low = memory_info::is_low_memory(low_mem_handle);

        let cfg = config.lock().unwrap();

        // Enforce cache cap continuously (not just during optimization)
        if cfg.cache_max_percent > 0 {
            let max_bytes = (info.total_physical_bytes as f64 * cfg.cache_max_percent as f64
                / 100.0) as u64;
            if info.cached_bytes > max_bytes {
                let _ = optimizer::set_file_cache_hard_max(max_bytes);
            }
        }

        // Auto-optimize or forced optimize
        if force_optimize {
            drop(cfg);
            let cfg2 = config.lock().unwrap();
            let result = optimizer::optimize_all(
                cfg2.level,
                cfg2.cache_max_percent,
                &cfg2.excluded_processes,
            );
            last_optimization = Instant::now();
            let _ = event_tx.send(MonitorEvent::OptimizationCompleted(result));
        } else if cfg.auto_optimize_enabled {
            // Check cooldown
            let cooldown_elapsed =
                last_optimization.elapsed().as_secs() >= cfg.cooldown_seconds as u64;
            let threshold_breached = info.usage_percent() >= cfg.threshold_percent as f64;

            if cooldown_elapsed && (is_low || threshold_breached) {
                let level = cfg.level;
                let cache_pct = cfg.cache_max_percent;
                let excluded = cfg.excluded_processes.clone();
                drop(cfg);

                let result = optimizer::optimize_all(level, cache_pct, &excluded);
                last_optimization = Instant::now();
                let _ = event_tx.send(MonitorEvent::OptimizationCompleted(result));
            }
        }
    }

    // Cleanup
    unsafe {
        if !low_mem_handle.is_invalid() {
            let _ = windows::Win32::Foundation::CloseHandle(low_mem_handle);
        }
        if !_high_mem_handle.is_invalid() {
            let _ = windows::Win32::Foundation::CloseHandle(_high_mem_handle);
        }
    }
}

/// Set the current thread to low memory priority + EcoQoS.
fn set_thread_low_priority() {
    unsafe {
        use std::mem::size_of;
        let thread = windows::Win32::System::Threading::GetCurrentThread();

        // Thread memory priority = LOW (2)
        #[repr(C)]
        struct MemPrio {
            priority: u32,
        }
        let mut info = MemPrio { priority: 2 };
        let _ = windows::Win32::System::Threading::SetThreadInformation(
            thread,
            windows::Win32::System::Threading::ThreadMemoryPriority,
            &mut info as *mut _ as *const std::ffi::c_void,
            size_of::<MemPrio>() as u32,
        );

        // Thread EcoQoS
        #[repr(C)]
        struct PowerThrottle {
            version: u32,
            control_mask: u32,
            state_mask: u32,
        }
        let mut pt = PowerThrottle {
            version: 1,
            control_mask: 1,
            state_mask: 1,
        };
        let _ = windows::Win32::System::Threading::SetThreadInformation(
            thread,
            windows::Win32::System::Threading::ThreadPowerThrottling,
            &mut pt as *mut _ as *const std::ffi::c_void,
            size_of::<PowerThrottle>() as u32,
        );
    }
}
