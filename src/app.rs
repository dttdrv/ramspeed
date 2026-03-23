use std::collections::HashSet;
use std::sync::mpsc;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

use eframe::egui;

use crate::graph::UsageHistory;
use crate::memory_info::MemoryInfo;
use crate::monitor::{MonitorCommand, MonitorEvent};
use crate::optimizer::OptimizationResult;
use crate::process_list::ProcessInfo;
use crate::settings::{OptimizationLevel, Settings, SettingsHandle};

pub struct RamSpeedApp {
    // Settings
    settings_handle: SettingsHandle,
    settings_cache: Settings,

    // Monitor communication
    event_rx: mpsc::Receiver<MonitorEvent>,
    cmd_tx: mpsc::Sender<MonitorCommand>,

    // State
    memory_info: MemoryInfo,
    usage_history: UsageHistory,
    optimization_history: Vec<OptimizationResult>,
    processes: Vec<ProcessInfo>,
    is_optimizing: bool,
    status_text: String,
    total_freed_mb: f64,
    optimization_count: u32,

    // UI state
    selected_tab: Tab,
    process_refresh_requested: bool,
    last_process_refresh: Instant,

    // Admin status
    is_admin: bool,

    // Tray
    tray_icon: Option<tray_icon::TrayIcon>,
    tray_show_requested: Arc<AtomicBool>,
    tray_quit_requested: Arc<AtomicBool>,
    tray_optimize_requested: Arc<AtomicBool>,
    is_hidden: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Tab {
    Processes,
    History,
}

impl RamSpeedApp {
    pub fn new(
        settings_handle: SettingsHandle,
        event_rx: mpsc::Receiver<MonitorEvent>,
        cmd_tx: mpsc::Sender<MonitorCommand>,
        is_admin: bool,
    ) -> Self {
        let settings_cache = settings_handle.get();

        // Build tray icon
        let tray_show = Arc::new(AtomicBool::new(false));
        let tray_quit = Arc::new(AtomicBool::new(false));
        let tray_optimize = Arc::new(AtomicBool::new(false));
        let tray_icon = Self::create_tray_icon(
            tray_show.clone(),
            tray_quit.clone(),
            tray_optimize.clone(),
        );

        Self {
            settings_handle,
            settings_cache,
            event_rx,
            cmd_tx,
            memory_info: MemoryInfo::default(),
            usage_history: UsageHistory::new(150),
            optimization_history: Vec::new(),
            processes: Vec::new(),
            is_optimizing: false,
            status_text: if is_admin {
                "Ready".into()
            } else {
                "Running without admin — some features unavailable".into()
            },
            total_freed_mb: 0.0,
            optimization_count: 0,
            selected_tab: Tab::Processes,
            process_refresh_requested: true,
            last_process_refresh: Instant::now() - std::time::Duration::from_secs(60),
            is_admin,
            tray_icon,
            tray_show_requested: tray_show,
            tray_quit_requested: tray_quit,
            tray_optimize_requested: tray_optimize,
            is_hidden: false,
        }
    }

    fn create_tray_icon(
        show_flag: Arc<AtomicBool>,
        quit_flag: Arc<AtomicBool>,
        optimize_flag: Arc<AtomicBool>,
    ) -> Option<tray_icon::TrayIcon> {
        use tray_icon::TrayIconBuilder;
        use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};

        let menu = Menu::new();
        let show_item = MenuItem::new("Show RAMSpeed", true, None);
        let optimize_item = MenuItem::new("Optimize Now", true, None);
        let quit_item = MenuItem::new("Exit", true, None);

        let _ = menu.append(&show_item);
        let _ = menu.append(&optimize_item);
        let _ = menu.append(&PredefinedMenuItem::separator());
        let _ = menu.append(&quit_item);

        let show_id = show_item.id().clone();
        let quit_id = quit_item.id().clone();
        let optimize_id = optimize_item.id().clone();

        // Menu event handler
        let sf = show_flag.clone();
        let qf = quit_flag.clone();
        let of = optimize_flag.clone();
        MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
            if event.id == show_id {
                sf.store(true, Ordering::SeqCst);
            } else if event.id == quit_id {
                qf.store(true, Ordering::SeqCst);
            } else if event.id == optimize_id {
                of.store(true, Ordering::SeqCst);
            }
        }));

        // Load icon for tray
        let icon = Self::load_tray_icon();

        TrayIconBuilder::new()
            .with_tooltip("RAMSpeed — Memory Optimizer")
            .with_menu(Box::new(menu))
            .with_icon(icon)
            .build()
            .ok()
    }

    fn load_tray_icon() -> tray_icon::Icon {
        let icon_bytes = include_bytes!("RAMSpeed/Resources/app.ico");
        if let Ok(img) = image::load_from_memory(icon_bytes) {
            let rgba = img.to_rgba8();
            let (w, h) = rgba.dimensions();
            if let Ok(icon) = tray_icon::Icon::from_rgba(rgba.into_raw(), w, h) {
                return icon;
            }
        }
        // Fallback: 16x16 green
        let mut rgba = Vec::with_capacity(16 * 16 * 4);
        for _ in 0..16 * 16 {
            rgba.extend_from_slice(&[80, 160, 80, 255]);
        }
        tray_icon::Icon::from_rgba(rgba, 16, 16).unwrap()
    }

    fn process_events(&mut self) {
        while let Ok(event) = self.event_rx.try_recv() {
            match event {
                MonitorEvent::MemoryUpdated(info) => {
                    self.usage_history.push(info.usage_percent());
                    self.memory_info = info;
                }
                MonitorEvent::OptimizationCompleted(result) => {
                    self.is_optimizing = false;
                    if result.success {
                        self.total_freed_mb += result.freed_mb();
                        self.optimization_count += 1;
                        self.status_text = format!(
                            "Freed {:.1} MB in {:.0}ms",
                            result.freed_mb(),
                            result.duration_ms
                        );
                    } else {
                        self.status_text = format!(
                            "Optimization failed: {}",
                            result.error_message.as_deref().unwrap_or("unknown")
                        );
                    }
                    self.optimization_history.insert(0, result);
                    let max = self.settings_cache.history_max_items;
                    self.optimization_history.truncate(max);
                }
            }
        }
    }

    fn save_setting(&mut self, f: impl FnOnce(&mut Settings)) {
        self.settings_handle.update(f);
        self.settings_cache = self.settings_handle.get();
        let _ = self.cmd_tx.send(MonitorCommand::UpdateSettings);
    }

    fn render_menu_bar(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        egui::menu::bar(ui, |ui| {
            ui.menu_button("File", |ui| {
                if ui.button("Exit").clicked() {
                    ui.close_menu();
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                }
            });
            ui.menu_button("Tools", |ui| {
                if ui
                    .add_enabled(!self.is_optimizing, egui::Button::new("Optimize Now"))
                    .clicked()
                {
                    ui.close_menu();
                    self.is_optimizing = true;
                    self.status_text = "Optimizing...".into();
                    let _ = self.cmd_tx.send(MonitorCommand::OptimizeNow);
                }
                if ui.button("Refresh Processes").clicked() {
                    ui.close_menu();
                    self.process_refresh_requested = true;
                }
                ui.separator();
                let mut auto = self.settings_cache.auto_optimize_enabled;
                if ui.checkbox(&mut auto, "Auto-Optimize").changed() {
                    let val = auto;
                    self.save_setting(|s| s.auto_optimize_enabled = val);
                    self.status_text = if val {
                        format!(
                            "Auto-optimizing (threshold: {}%)",
                            self.settings_cache.threshold_percent
                        )
                    } else {
                        "Auto-optimize off".into()
                    };
                }
            });
            ui.menu_button("Help", |ui| {
                if ui.button("About RAMSpeed").clicked() {
                    ui.close_menu();
                    // Simple about text shown in status
                    self.status_text =
                        "RAMSpeed v1.0 — Memory Optimizer (Rust/egui)".into();
                }
            });

            // Right-aligned admin status
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if self.is_admin {
                    ui.colored_label(egui::Color32::from_rgb(80, 180, 80), "⬢ Admin");
                } else {
                    ui.colored_label(egui::Color32::from_rgb(200, 160, 50), "⬡ Standard");
                }
            });
        });
    }

    fn render_header(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            ui.vertical(|ui| {
                ui.horizontal(|ui| {
                    ui.label("RAM: ");
                    ui.strong(format!("{:.1} GB", self.memory_info.used_gb()));
                    ui.label(format!(" / {:.1} GB total", self.memory_info.total_gb()));
                    ui.strong(format!("({:.0}%)", self.memory_info.usage_percent()));
                    ui.label("  Available: ");
                    ui.strong(format!("{:.1} GB", self.memory_info.available_gb()));
                });
                let pct = self.memory_info.usage_percent() as f32 / 100.0;
                let color = if pct > 0.9 {
                    egui::Color32::from_rgb(220, 60, 60)
                } else if pct > 0.75 {
                    egui::Color32::from_rgb(220, 180, 50)
                } else {
                    egui::Color32::from_rgb(80, 160, 80)
                };
                let bar = egui::ProgressBar::new(pct).fill(color);
                ui.add(bar);
            });
            ui.add_space(8.0);
            if ui
                .add_enabled(
                    !self.is_optimizing,
                    egui::Button::new("⚡ Optimize Now").min_size(egui::vec2(110.0, 32.0)),
                )
                .clicked()
            {
                self.is_optimizing = true;
                self.status_text = "Optimizing...".into();
                let _ = self.cmd_tx.send(MonitorCommand::OptimizeNow);
            }
        });
    }

    fn render_settings_panel(&mut self, ui: &mut egui::Ui) {
        ui.heading("Settings");
        ui.add_space(4.0);

        let mut auto = self.settings_cache.auto_optimize_enabled;
        if ui.checkbox(&mut auto, "Auto-Optimize").changed() {
            let val = auto;
            self.save_setting(|s| s.auto_optimize_enabled = val);
        }
        ui.add_space(4.0);

        // Level
        ui.label("Level");
        let levels = [
            OptimizationLevel::Conservative,
            OptimizationLevel::Balanced,
            OptimizationLevel::Aggressive,
        ];
        let current_idx = levels
            .iter()
            .position(|l| *l == self.settings_cache.level)
            .unwrap_or(1);
        let mut sel = current_idx;
        egui::ComboBox::from_id_salt("level_combo")
            .selected_text(format!("{}", levels[sel]))
            .show_ui(ui, |ui| {
                for (i, lvl) in levels.iter().enumerate() {
                    ui.selectable_value(&mut sel, i, format!("{lvl}"));
                }
            });
        if sel != current_idx {
            let new_level = levels[sel];
            self.save_setting(|s| s.level = new_level);
        }
        ui.add_space(4.0);

        // Threshold
        let mut threshold = self.settings_cache.threshold_percent as f32;
        ui.horizontal(|ui| {
            ui.label("Threshold");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                ui.label(format!("{:.0}%", threshold));
            });
        });
        if ui
            .add(egui::Slider::new(&mut threshold, 10.0..=95.0).show_value(false))
            .changed()
        {
            let val = threshold as u32;
            self.save_setting(|s| s.threshold_percent = val);
        }

        // Cooldown
        let mut cooldown = self.settings_cache.cooldown_seconds as f32;
        ui.horizontal(|ui| {
            ui.label("Cooldown");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                ui.label(format!("{:.0}s", cooldown));
            });
        });
        if ui
            .add(egui::Slider::new(&mut cooldown, 5.0..=300.0).show_value(false))
            .changed()
        {
            let val = cooldown as u32;
            self.save_setting(|s| s.cooldown_seconds = val);
        }

        // Check interval
        let mut interval = self.settings_cache.check_interval_seconds as f32;
        ui.horizontal(|ui| {
            ui.label("Check Interval");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                ui.label(format!("{:.0}s", interval));
            });
        });
        if ui
            .add(egui::Slider::new(&mut interval, 1.0..=60.0).show_value(false))
            .changed()
        {
            let val = interval as u32;
            self.save_setting(|s| s.check_interval_seconds = val);
        }

        // Cache cap
        let mut cache_pct = self.settings_cache.cache_max_percent as f32;
        ui.horizontal(|ui| {
            ui.label("Cache Cap");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if cache_pct == 0.0 {
                    ui.label("Off");
                } else {
                    ui.label(format!("{:.0}%", cache_pct));
                }
            });
        });
        if ui
            .add(egui::Slider::new(&mut cache_pct, 0.0..=75.0).show_value(false))
            .changed()
        {
            let val = cache_pct as u32;
            self.save_setting(|s| s.cache_max_percent = val);
        }

        // Self WS cap
        let mut ws_cap = self.settings_cache.self_working_set_cap_mb as f32;
        ui.horizontal(|ui| {
            ui.label("Self WS Cap");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                ui.label(format!("{:.0} MB", ws_cap));
            });
        });
        if ui
            .add(egui::Slider::new(&mut ws_cap, 5.0..=100.0).show_value(false))
            .changed()
        {
            let val = ws_cap as u32;
            self.save_setting(|s| s.self_working_set_cap_mb = val);
        }

        ui.add_space(8.0);
        ui.separator();
        ui.add_space(4.0);

        let mut start_win = self.settings_cache.start_with_windows;
        if ui.checkbox(&mut start_win, "Start with Windows").changed() {
            let val = start_win;
            self.save_setting(|s| s.start_with_windows = val);
            if val {
                if let Some(exe) = std::env::current_exe().ok().and_then(|p| p.to_str().map(String::from)) {
                    crate::task_scheduler::create_task_with_logon(&exe);
                }
            } else {
                crate::task_scheduler::delete_task();
            }
        }

        let mut tray = self.settings_cache.minimize_to_tray;
        if ui
            .checkbox(&mut tray, "Minimize to Tray on Close")
            .changed()
        {
            let val = tray;
            self.save_setting(|s| s.minimize_to_tray = val);
        }
    }

    fn render_memory_stats(&self, ui: &mut egui::Ui) {
        ui.group(|ui| {
            ui.heading("Memory");
            ui.columns(4, |cols| {
                let stats: &[(&&str, String)] = &[
                    (&"Used", format!("{:.2} GB", self.memory_info.used_gb())),
                    (&"Available", format!("{:.2} GB", self.memory_info.available_gb())),
                    (&"Cached", format!("{:.2} GB", self.memory_info.cached_gb())),
                    (&"Modified", format!("{:.2} GB", self.memory_info.modified_gb())),
                ];
                for (i, (label, value)) in stats.iter().enumerate() {
                    cols[i].small(**label);
                    cols[i].strong(value);
                }
            });
            ui.columns(4, |cols| {
                let stats: &[(&&str, String)] = &[
                    (&"Free", format!("{:.2} GB", self.memory_info.free_gb())),
                    (
                        &"Compressed",
                        format!("{:.0} MB", self.memory_info.compressed_mb()),
                    ),
                    (
                        &"Commit",
                        format!("{:.0}%", self.memory_info.commit_percent()),
                    ),
                    (&"Processes", format!("{}", self.memory_info.process_count)),
                ];
                for (i, (label, value)) in stats.iter().enumerate() {
                    cols[i].small(**label);
                    cols[i].strong(value);
                }
            });
        });
    }

    fn render_graph(&self, ui: &mut egui::Ui) {
        ui.group(|ui| {
            ui.heading("Usage History");
            let points = self.usage_history.points();
            if points.len() >= 2 {
                let desired = egui::vec2(ui.available_width(), 90.0);
                let (rect, _) = ui.allocate_exact_size(desired, egui::Sense::hover());

                let painter = ui.painter_at(rect);

                // Grid lines at 25%, 50%, 75%
                for pct in [25.0, 50.0, 75.0] {
                    let y = rect.top() + rect.height() * (1.0 - pct / 100.0);
                    painter.line_segment(
                        [egui::pos2(rect.left(), y), egui::pos2(rect.right(), y)],
                        egui::Stroke::new(0.5, egui::Color32::from_gray(200)),
                    );
                }

                // Build line points
                let n = points.len();
                let line_points: Vec<egui::Pos2> = points
                    .iter()
                    .enumerate()
                    .map(|(i, &pct)| {
                        let x = rect.left() + (i as f32 / (n - 1) as f32) * rect.width();
                        let y = rect.top() + rect.height() * (1.0 - pct as f32 / 100.0);
                        egui::pos2(x, y)
                    })
                    .collect();

                // Draw line
                let stroke = egui::Stroke::new(1.5, egui::Color32::from_rgb(50, 120, 200));
                for window in line_points.windows(2) {
                    painter.line_segment([window[0], window[1]], stroke);
                }

                // Labels
                let label_color = egui::Color32::from_gray(150);
                painter.text(
                    egui::pos2(rect.left() + 2.0, rect.top()),
                    egui::Align2::LEFT_TOP,
                    "100%",
                    egui::FontId::proportional(9.0),
                    label_color,
                );
                painter.text(
                    egui::pos2(rect.left() + 2.0, rect.top() + rect.height() * 0.5),
                    egui::Align2::LEFT_CENTER,
                    "50%",
                    egui::FontId::proportional(9.0),
                    label_color,
                );
                painter.text(
                    egui::pos2(rect.left() + 2.0, rect.bottom()),
                    egui::Align2::LEFT_BOTTOM,
                    "0%",
                    egui::FontId::proportional(9.0),
                    label_color,
                );
            } else {
                ui.allocate_space(egui::vec2(ui.available_width(), 90.0));
                ui.label("Collecting data...");
            }
        });
    }

    fn render_process_tab(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            if ui.button("🔄 Refresh").clicked() {
                self.process_refresh_requested = true;
            }
        });

        // Refresh processes in background if requested (debounced)
        if self.process_refresh_requested
            && self.last_process_refresh.elapsed() > std::time::Duration::from_millis(500)
        {
            let excluded: HashSet<String> = self
                .settings_cache
                .excluded_processes
                .iter()
                .map(|s| s.to_lowercase())
                .collect();
            self.processes = crate::process_list::get_top_processes(&excluded, 100);
            self.process_refresh_requested = false;
            self.last_process_refresh = Instant::now();
        }

        egui::ScrollArea::vertical().show(ui, |ui| {
            egui::Grid::new("process_grid")
                .num_columns(5)
                .striped(true)
                .min_col_width(40.0)
                .show(ui, |ui| {
                    ui.strong("Process");
                    ui.strong("PID");
                    ui.strong("Working Set");
                    ui.strong("Private");
                    ui.strong("Excl.");
                    ui.end_row();

                    let mut toggle_idx = None;
                    for (idx, proc) in self.processes.iter().enumerate() {
                        ui.label(&proc.name);
                        ui.label(format!("{}", proc.pid));
                        ui.label(format!("{:.0} MB", proc.working_set_mb()));
                        ui.label(format!("{:.0} MB", proc.private_mb()));
                        let mut excl = proc.is_excluded;
                        if ui.checkbox(&mut excl, "").changed() {
                            toggle_idx = Some((idx, excl));
                        }
                        ui.end_row();
                    }

                    // Handle toggle outside the borrow
                    if let Some((idx, new_val)) = toggle_idx {
                        if let Some(proc) = self.processes.get_mut(idx) {
                            proc.is_excluded = new_val;
                            let name = proc.name.clone();
                            if new_val {
                                self.settings_handle.update(|s| {
                                    if !s.excluded_processes.iter().any(|n| n.eq_ignore_ascii_case(&name)) {
                                        s.excluded_processes.push(name.clone());
                                    }
                                });
                            } else {
                                self.settings_handle.update(|s| {
                                    s.excluded_processes.retain(|n| !n.eq_ignore_ascii_case(&name));
                                });
                            }
                            self.settings_cache = self.settings_handle.get();
                            let _ = self.cmd_tx.send(MonitorCommand::UpdateSettings);
                        }
                    }
                });
        });
    }

    fn render_history_tab(&self, ui: &mut egui::Ui) {
        if self.optimization_history.is_empty() {
            ui.label("No optimization runs yet.");
            return;
        }

        egui::ScrollArea::vertical().show(ui, |ui| {
            egui::Grid::new("history_grid")
                .num_columns(4)
                .striped(true)
                .show(ui, |ui| {
                    ui.strong("Result");
                    ui.strong("Freed");
                    ui.strong("Duration");
                    ui.strong("Methods");
                    ui.end_row();

                    for result in &self.optimization_history {
                        if result.success {
                            ui.colored_label(
                                egui::Color32::from_rgb(80, 180, 80),
                                "✓ Success",
                            );
                        } else {
                            ui.colored_label(
                                egui::Color32::from_rgb(220, 60, 60),
                                "✗ Failed",
                            );
                        }
                        ui.label(format!("{:.1} MB", result.freed_mb()));
                        ui.label(format!("{:.0} ms", result.duration_ms));
                        ui.label(result.methods_used.join(", "));
                        ui.end_row();
                    }
                });
        });
    }

    fn render_status_bar(&self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            ui.label(format!("Status: {}", self.status_text));
            ui.separator();
            ui.label(format!(
                "Freed: {:.0} MB ({} runs)",
                self.total_freed_mb, self.optimization_count
            ));
            ui.separator();
            ui.label(format!(
                "Auto: {}  Level: {}",
                if self.settings_cache.auto_optimize_enabled {
                    "On"
                } else {
                    "Off"
                },
                self.settings_cache.level
            ));
        });
    }
}

impl eframe::App for RamSpeedApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // --- Tray interactions ---
        // Check if tray requested quit
        if self.tray_quit_requested.load(Ordering::SeqCst) {
            self.tray_quit_requested.store(false, Ordering::SeqCst);
            // Drop tray icon before closing
            self.tray_icon.take();
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }

        // Check if tray requested show
        if self.tray_show_requested.load(Ordering::SeqCst) {
            self.tray_show_requested.store(false, Ordering::SeqCst);
            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
            ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
            self.is_hidden = false;
        }

        // Check if tray requested optimize
        if self.tray_optimize_requested.load(Ordering::SeqCst) {
            self.tray_optimize_requested.store(false, Ordering::SeqCst);
            if !self.is_optimizing {
                self.is_optimizing = true;
                self.status_text = "Optimizing...".into();
                let _ = self.cmd_tx.send(MonitorCommand::OptimizeNow);
            }
        }

        // --- Handle close (minimize to tray instead) ---
        if ctx.input(|i| i.viewport().close_requested()) && self.settings_cache.minimize_to_tray {
            // Cancel the close and hide to tray instead
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
            self.is_hidden = true;

            // Update tray tooltip with current usage
            if let Some(ref tray) = self.tray_icon {
                let _ = tray.set_tooltip(Some(&format!(
                    "RAMSpeed — {:.0}% ({:.1}/{:.1} GB)",
                    self.memory_info.usage_percent(),
                    self.memory_info.used_gb(),
                    self.memory_info.total_gb()
                )));
            }
        }

        // --- Save window geometry when visible ---
        if !self.is_hidden {
            if let Some(rect) = ctx.input(|i| i.viewport().inner_rect) {
                let w = rect.width();
                let h = rect.height();
                let x = rect.left();
                let y = rect.top();
                let sc = &self.settings_cache;
                if (w - sc.window_width).abs() > 2.0
                    || (h - sc.window_height).abs() > 2.0
                    || (x - sc.window_x).abs() > 2.0
                    || (y - sc.window_y).abs() > 2.0
                {
                    self.save_setting(|s| {
                        s.window_width = w;
                        s.window_height = h;
                        s.window_x = x;
                        s.window_y = y;
                    });
                }
            }
        }

        // Poll monitor events
        self.process_events();

        // Request repaint at the monitor interval rate
        ctx.request_repaint_after(std::time::Duration::from_secs(1));

        // Update tray tooltip periodically
        if !self.is_hidden {
            if let Some(ref tray) = self.tray_icon {
                let _ = tray.set_tooltip(Some(&format!(
                    "RAMSpeed — {:.0}% ({:.1}/{:.1} GB)",
                    self.memory_info.usage_percent(),
                    self.memory_info.used_gb(),
                    self.memory_info.total_gb()
                )));
            }
        }

        // Top menu bar
        egui::TopBottomPanel::top("menu_bar").show(ctx, |ui| {
            self.render_menu_bar(ui, ctx);
        });

        // Bottom status bar
        egui::TopBottomPanel::bottom("status_bar").show(ctx, |ui| {
            self.render_status_bar(ui);
        });

        // Left settings panel
        egui::SidePanel::left("settings_panel")
            .default_width(210.0)
            .resizable(true)
            .show(ctx, |ui| {
                egui::ScrollArea::vertical().show(ui, |ui| {
                    self.render_settings_panel(ui);
                });
            });

        // Central panel
        egui::CentralPanel::default().show(ctx, |ui| {
            // Header with RAM usage bar
            self.render_header(ui);
            ui.add_space(4.0);

            // Memory stats
            self.render_memory_stats(ui);
            ui.add_space(4.0);

            // Graph
            self.render_graph(ui);
            ui.add_space(4.0);

            // Tabs
            ui.horizontal(|ui| {
                ui.selectable_value(&mut self.selected_tab, Tab::Processes, "Top Processes");
                ui.selectable_value(&mut self.selected_tab, Tab::History, "History");
            });
            ui.separator();

            match self.selected_tab {
                Tab::Processes => self.render_process_tab(ui),
                Tab::History => self.render_history_tab(ui),
            }
        });
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        // Remove tray icon
        self.tray_icon.take();
        let _ = self.cmd_tx.send(MonitorCommand::Stop);
        self.settings_handle.flush();
    }
}
