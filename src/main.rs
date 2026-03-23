#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod graph;
mod memory_info;
mod monitor;
mod optimizer;
mod privilege;
mod process_list;
mod settings;
mod task_scheduler;

use settings::{Settings, SettingsHandle};

fn main() {
    let args: Vec<String> = std::env::args().collect();

    // Single-instance check
    let instance = single_instance::SingleInstance::new("RAMSpeed_SingleInstance_Rust_B7F3A2")
        .expect("Failed to create single-instance lock");
    if !instance.is_single() {
        activate_existing_window();
        return;
    }

    let is_admin = is_running_as_admin();

    // Handle --setup-task: create the scheduled task and exit
    if args.contains(&"--setup-task".to_string()) {
        if is_admin {
            if let Some(exe) = std::env::current_exe().ok() {
                task_scheduler::create_task(&exe.to_string_lossy());
            }
        }
        return;
    }

    // Handle elevation flow
    if !is_admin && !args.contains(&"--elevated".to_string()) {
        if task_scheduler::task_exists() {
            if task_scheduler::run_task() {
                return;
            }
        }

        // No task or task run failed — try to create via UAC
        if let Some(exe) = std::env::current_exe().ok() {
            let exe_str = exe.to_string_lossy().to_string();
            if run_as_admin(&exe_str, "--setup-task") {
                std::thread::sleep(std::time::Duration::from_millis(2000));
                if task_scheduler::task_exists() && task_scheduler::run_task() {
                    return;
                }
            }
        }
        // If all elevation attempts fail, continue without admin
    }

    // Self-optimization: low memory priority + EcoQoS
    optimizer::set_self_low_priority();

    // Enable required privileges (will silently fail without admin)
    let privs = privilege::enable_all_required();

    // Load settings
    let settings = Settings::load();
    let settings_handle = SettingsHandle::new(settings);

    // Start background monitor
    let (event_rx, cmd_tx) = monitor::start_monitor(settings_handle.clone());

    // Create and run the eframe app
    let settings_snapshot = settings_handle.get();
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("RAMSpeed — Memory Optimizer")
        .with_inner_size([
            settings_snapshot.window_width,
            settings_snapshot.window_height,
        ])
        .with_min_inner_size([720.0, 520.0])
        .with_close_button(true)
        .with_icon(load_icon());

    // Restore saved window position if valid
    if settings_snapshot.window_x.is_finite() && settings_snapshot.window_y.is_finite() {
        viewport = viewport.with_position([
            settings_snapshot.window_x,
            settings_snapshot.window_y,
        ]);
    }

    let native_options = eframe::NativeOptions {
        viewport,
        ..Default::default()
    };

    let sh = settings_handle.clone();
    let _ = eframe::run_native(
        "RAMSpeed",
        native_options,
        Box::new(move |_cc| {
            Ok(Box::new(app::RamSpeedApp::new(
                sh,
                event_rx,
                cmd_tx,
                is_admin || privs.has_all(),
            )))
        }),
    );

    // Flush settings on exit
    settings_handle.flush();
}

fn is_running_as_admin() -> bool {
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::Security::{
        GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY,
    };
    use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    unsafe {
        let mut token = windows::Win32::Foundation::HANDLE::default();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
            return false;
        }

        let mut elevation = TOKEN_ELEVATION::default();
        let mut ret_len = 0u32;
        let result = GetTokenInformation(
            token,
            TokenElevation,
            Some(&mut elevation as *mut _ as *mut std::ffi::c_void),
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut ret_len,
        );
        let _ = CloseHandle(token);

        result.is_ok() && elevation.TokenIsElevated != 0
    }
}

fn activate_existing_window() {
    use windows::Win32::UI::WindowsAndMessaging::{
        FindWindowW, SetForegroundWindow, ShowWindow, SW_RESTORE,
    };
    use windows::core::w;

    unsafe {
        if let Ok(hwnd) = FindWindowW(None, w!("RAMSpeed — Memory Optimizer")) {
            if !hwnd.is_invalid() {
                let _ = ShowWindow(hwnd, SW_RESTORE);
                let _ = SetForegroundWindow(hwnd);
            }
        }
    }
}

fn run_as_admin(exe: &str, args: &str) -> bool {
    use windows::Win32::UI::Shell::ShellExecuteW;
    use windows::core::{w, PCWSTR};

    let exe_wide: Vec<u16> = exe.encode_utf16().chain(std::iter::once(0)).collect();
    let args_wide: Vec<u16> = args.encode_utf16().chain(std::iter::once(0)).collect();

    unsafe {
        let result = ShellExecuteW(
            None,
            w!("runas"),
            PCWSTR(exe_wide.as_ptr()),
            PCWSTR(args_wide.as_ptr()),
            None,
            windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL,
        );
        result.0 as usize > 32
    }
}

fn load_icon() -> egui::IconData {
    let icon_bytes = include_bytes!("RAMSpeed/Resources/app.ico");
    if let Ok(img) = image::load_from_memory(icon_bytes) {
        let rgba = img.to_rgba8();
        let (w, h) = rgba.dimensions();
        return egui::IconData {
            rgba: rgba.into_raw(),
            width: w,
            height: h,
        };
    }
    let mut rgba = Vec::with_capacity(16 * 16 * 4);
    for _ in 0..16 * 16 {
        rgba.extend_from_slice(&[80, 160, 80, 255]);
    }
    egui::IconData {
        rgba,
        width: 16,
        height: 16,
    }
}
