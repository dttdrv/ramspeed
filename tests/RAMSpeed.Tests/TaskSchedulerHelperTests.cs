using System.Xml.Linq;
using RAMSpeed.Services;

namespace RAMSpeed.Tests;

public class TaskSchedulerHelperTests
{
    [Fact]
    public void BuildTaskXml_creates_logon_trigger_with_highest_run_level()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\RAMSpeed\RAMSpeed.exe", startAtLogon: true);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.NotNull(document.Root?.Element(ns + "Triggers")?.Element(ns + "LogonTrigger"));
        Assert.Equal("HighestAvailable", document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "RunLevel")?.Value);
        Assert.Equal(@"C:\Program Files\RAMSpeed\RAMSpeed.exe", document.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "UserId")?.Value));
        Assert.Equal("InteractiveToken", document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "LogonType")?.Value);
    }

    [Fact]
    public void BuildTaskXml_omits_logon_trigger_when_startup_disabled()
    {
        var xml = TaskSchedulerHelper.BuildTaskXml(@"C:\Program Files\RAMSpeed\RAMSpeed.exe", startAtLogon: false);
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Null(document.Root?.Element(ns + "Triggers"));
        Assert.Equal("HighestAvailable", document.Root?.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "RunLevel")?.Value);
    }
}
