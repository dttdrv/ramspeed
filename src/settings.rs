use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Instant;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub enum OptimizationLevel {
    Conservative,
    Balanced,
    Aggressive,
}

impl std::fmt::Display for OptimizationLevel {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Conservative => write!(f, "Conservative"),
            Self::Balanced => write!(f, "Balanced"),
            Self::Aggressive => write!(f, "Aggressive"),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Settings {
    pub auto_optimize_enabled: bool,
    pub check_interval_seconds: u32,
    pub threshold_percent: u32,
    pub cooldown_seconds: u32,
    pub level: OptimizationLevel,
    pub excluded_processes: Vec<String>,
    pub start_with_windows: bool,
    pub minimize_to_tray: bool,
    pub self_working_set_cap_mb: u32,
    pub cache_max_percent: u32,
    pub window_width: f32,
    pub window_height: f32,
    pub window_x: f32,
    pub window_y: f32,
    pub history_max_items: usize,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            auto_optimize_enabled: false,
            check_interval_seconds: 5,
            threshold_percent: 80,
            cooldown_seconds: 30,
            level: OptimizationLevel::Balanced,
            excluded_processes: vec![
                "System".into(),
                "Idle".into(),
                "smss".into(),
                "csrss".into(),
                "wininit".into(),
                "services".into(),
                "lsass".into(),
                "svchost".into(),
                "dwm".into(),
                "winlogon".into(),
                "Memory Compression".into(),
                "Registry".into(),
                "fontdrvhost".into(),
                "conhost".into(),
            ],
            start_with_windows: false,
            minimize_to_tray: true,
            self_working_set_cap_mb: 25,
            cache_max_percent: 0,
            window_width: 960.0,
            window_height: 660.0,
            window_x: f32::NAN,
            window_y: f32::NAN,
            history_max_items: 50,
        }
    }
}

impl Settings {
    fn settings_path() -> PathBuf {
        let base = dirs::config_dir().unwrap_or_else(|| PathBuf::from("."));
        base.join("RAMSpeed").join("settings.json")
    }

    pub fn load() -> Self {
        let path = Self::settings_path();
        if path.exists() {
            if let Ok(data) = std::fs::read_to_string(&path) {
                if let Ok(s) = serde_json::from_str(&data) {
                    return s;
                }
            }
        }
        Self::default()
    }

    pub fn save(&self) {
        let path = Self::settings_path();
        if let Some(dir) = path.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        if let Ok(json) = serde_json::to_string_pretty(self) {
            let _ = std::fs::write(&path, json);
        }
    }
}

/// Thread-safe settings handle with debounced saving.
#[derive(Clone)]
pub struct SettingsHandle {
    inner: Arc<Mutex<SettingsInner>>,
}

struct SettingsInner {
    settings: Settings,
    dirty: bool,
    last_save: Instant,
}

const SAVE_DEBOUNCE_MS: u128 = 500;

impl SettingsHandle {
    pub fn new(settings: Settings) -> Self {
        Self {
            inner: Arc::new(Mutex::new(SettingsInner {
                settings,
                dirty: false,
                last_save: Instant::now(),
            })),
        }
    }

    pub fn get(&self) -> Settings {
        self.inner.lock().unwrap().settings.clone()
    }

    pub fn update(&self, f: impl FnOnce(&mut Settings)) {
        let mut inner = self.inner.lock().unwrap();
        f(&mut inner.settings);
        inner.dirty = true;
        // Debounce: only save if enough time has passed
        if inner.last_save.elapsed().as_millis() >= SAVE_DEBOUNCE_MS {
            inner.settings.save();
            inner.dirty = false;
            inner.last_save = Instant::now();
        }
    }

    /// Force save if there are pending changes (call on exit).
    pub fn flush(&self) {
        let mut inner = self.inner.lock().unwrap();
        if inner.dirty {
            inner.settings.save();
            inner.dirty = false;
            inner.last_save = Instant::now();
        }
    }
}
