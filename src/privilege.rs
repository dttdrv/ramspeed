use windows::Win32::Foundation::{CloseHandle, HANDLE, LUID};
use windows::Win32::Security::{
    AdjustTokenPrivileges, LookupPrivilegeValueW, SE_PRIVILEGE_ENABLED,
    TOKEN_ADJUST_PRIVILEGES, TOKEN_PRIVILEGES, TOKEN_QUERY,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::core::PCWSTR;

/// Enable a named privilege on the current process token.
fn enable_privilege(name: &str) -> bool {
    unsafe {
        let mut token = HANDLE::default();
        if OpenProcessToken(
            GetCurrentProcess(),
            TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
            &mut token,
        )
        .is_err()
        {
            return false;
        }

        let wide: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();
        let mut luid = LUID::default();
        if LookupPrivilegeValueW(PCWSTR::null(), PCWSTR(wide.as_ptr()), &mut luid).is_err() {
            let _ = CloseHandle(token);
            return false;
        }

        let mut tp = TOKEN_PRIVILEGES {
            PrivilegeCount: 1,
            ..Default::default()
        };
        tp.Privileges[0].Luid = luid;
        tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;

        let result = AdjustTokenPrivileges(token, false, Some(&tp), 0, None, None);
        let _ = CloseHandle(token);
        result.is_ok()
    }
}

/// Enable all privileges required for memory optimization.
/// Returns which privileges were successfully enabled.
pub fn enable_all_required() -> PrivilegeStatus {
    let mut status = PrivilegeStatus::default();
    status.debug = enable_privilege("SeDebugPrivilege");
    status.profile = enable_privilege("SeProfileSingleProcessPrivilege");
    status.quota = enable_privilege("SeIncreaseQuotaPrivilege");
    status
}

#[derive(Debug, Default, Clone)]
pub struct PrivilegeStatus {
    pub debug: bool,
    pub profile: bool,
    pub quota: bool,
}

impl PrivilegeStatus {
    pub fn has_any(&self) -> bool {
        self.debug || self.profile || self.quota
    }

    pub fn has_all(&self) -> bool {
        self.debug && self.profile && self.quota
    }
}
