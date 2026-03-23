use std::os::windows::process::CommandExt;
use std::process::Command;

const TASK_NAME: &str = "RAMSpeed";

/// Check if the RAMSpeed scheduled task exists.
pub fn task_exists() -> bool {
    let output = Command::new("schtasks")
        .args(["/Query", "/TN", TASK_NAME])
        .creation_flags(0x08000000) // CREATE_NO_WINDOW
        .output();
    matches!(output, Ok(o) if o.status.success())
}

/// Create a scheduled task that runs the current exe with HIGHEST privileges.
/// This requires one-time admin for creation. After that, `/Run` works without UAC.
pub fn create_task(exe_path: &str) -> bool {
    // XML task definition for maximum control
    let xml = format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>RAMSpeed Memory Optimizer — elevated launch</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions>
    <Exec>
      <Command>{exe_path}</Command>
      <Arguments>--elevated</Arguments>
    </Exec>
  </Actions>
</Task>"#,
        exe_path = xml_escape(exe_path)
    );

    // Write XML to a temp file
    let temp_dir = std::env::temp_dir();
    let xml_path = temp_dir.join("ramspeed_task.xml");
    if std::fs::write(&xml_path, xml.as_bytes()).is_err() {
        return false;
    }

    let result = Command::new("schtasks")
        .args([
            "/Create",
            "/TN",
            TASK_NAME,
            "/XML",
            &xml_path.to_string_lossy(),
            "/F",
        ])
        .creation_flags(0x08000000)
        .output();

    let _ = std::fs::remove_file(&xml_path);

    matches!(result, Ok(o) if o.status.success())
}

/// Run the existing scheduled task (launches the app elevated without UAC).
pub fn run_task() -> bool {
    let output = Command::new("schtasks")
        .args(["/Run", "/TN", TASK_NAME])
        .creation_flags(0x08000000)
        .output();
    matches!(output, Ok(o) if o.status.success())
}

/// Delete the scheduled task.
pub fn delete_task() -> bool {
    let output = Command::new("schtasks")
        .args(["/Delete", "/TN", TASK_NAME, "/F"])
        .creation_flags(0x08000000)
        .output();
    matches!(output, Ok(o) if o.status.success())
}

/// Create a task with a LOGON trigger for "Start with Windows" + elevated.
pub fn create_task_with_logon(exe_path: &str) -> bool {
    let username = std::env::var("USERNAME").unwrap_or_default();
    let userdomain = std::env::var("USERDOMAIN").unwrap_or_default();
    let full_user = if userdomain.is_empty() {
        username.clone()
    } else {
        format!("{userdomain}\\{username}")
    };

    let xml = format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>RAMSpeed Memory Optimizer — elevated launch at logon</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions>
    <Exec>
      <Command>{exe_path}</Command>
      <Arguments>--elevated</Arguments>
    </Exec>
  </Actions>
</Task>"#,
        exe_path = xml_escape(exe_path),
        user = xml_escape(&full_user),
    );

    let temp_dir = std::env::temp_dir();
    let xml_path = temp_dir.join("ramspeed_task.xml");
    if std::fs::write(&xml_path, xml.as_bytes()).is_err() {
        return false;
    }

    let result = Command::new("schtasks")
        .args([
            "/Create",
            "/TN",
            TASK_NAME,
            "/XML",
            &xml_path.to_string_lossy(),
            "/F",
        ])
        .creation_flags(0x08000000)
        .output();

    let _ = std::fs::remove_file(&xml_path);

    matches!(result, Ok(o) if o.status.success())
}

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
