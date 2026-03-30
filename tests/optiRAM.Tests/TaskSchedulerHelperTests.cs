using System.Xml.Linq;
using optiRAM.Services;

namespace optiRAM.Tests;

public class TaskSchedulerHelperTests
{
    [Fact]
    public void BuildTaskXml_creates_logon_trigger_with_highest_run_level()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\optiRAM\optiRAM.exe", startAtLogon: true);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.NotNull(document.Root?.Element(ns + "Triggers")?.Element(ns + "LogonTrigger"));
        Assert.Equal("HighestAvailable", document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "RunLevel")?.Value);
        Assert.Equal(@"C:\Program Files\optiRAM\optiRAM.exe", document.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "UserId")?.Value));
        Assert.Equal("InteractiveToken", document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "LogonType")?.Value);
    }

    [Fact]
    public void BuildTaskXml_omits_logon_trigger_when_startup_disabled()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\optiRAM\optiRAM.exe", startAtLogon: false);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Null(document.Root?.Element(ns + "Triggers"));
        Assert.Equal("HighestAvailable", document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "RunLevel")?.Value);
    }

    [Fact]
    public void BuildTaskXml_includes_working_directory()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\optiRAM\optiRAM.exe", startAtLogon: false);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"C:\Program Files\optiRAM", document.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "WorkingDirectory")?.Value);
    }

    [Fact]
    public void BuildTaskXml_includes_author_in_registration_info()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\optiRAM\optiRAM.exe", startAtLogon: false);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("optiRAM", document.Root?.Element(ns + "RegistrationInfo")?.Element(ns + "Author")?.Value);
    }

    [Fact]
    public void BuildTaskXml_uses_sid_for_user_id()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\optiRAM\optiRAM.exe", startAtLogon: false);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var userId = document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "UserId")?.Value;

        // SID format: S-1-5-21-...
        Assert.StartsWith("S-1-", userId);
    }

    [Fact]
    public void BuildTaskXml_logon_trigger_includes_delay()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\optiRAM\optiRAM.exe", startAtLogon: true);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("PT15S", document.Root?.Element(ns + "Triggers")?.Element(ns + "LogonTrigger")?.Element(ns + "Delay")?.Value);
    }
}
