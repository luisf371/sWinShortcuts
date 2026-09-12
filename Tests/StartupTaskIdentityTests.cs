using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class StartupTaskIdentityTests
{
    private const string LegacyName = "sWinShortcuts_AutoStart";
    private const string OtherSid = "S-1-5-21-111111111-222222222-333333333-1001";
    private static string CurrentSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User!.Value;
        }
    }
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void GetState_ForeignLegacyTask_DoesNotEnableCurrentUserStartup()
    {
        var host = new TaskHost();
        host.Tasks[LegacyName] = TaskXml(OtherSid);
        Assert.False(host.Create().GetState().StartWithWindows);
        Assert.Empty(host.Mutations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Apply_ForeignLegacyTask_DoesNotChangeOtherUsersTask(bool enabled, bool admin)
    {
        var host = new TaskHost();
        var otherTask = TaskXml(OtherSid);
        host.Tasks[LegacyName] = otherTask;
        Assert.True(host.Create().Apply(enabled, admin, out var error), error);
        Assert.Equal(otherTask, host.Tasks[LegacyName]);
        Assert.DoesNotContain(host.Mutations, name => name == LegacyName);
        Assert.Equal(enabled && admin, host.Tasks.ContainsKey($"{LegacyName}_{CurrentSid}"));
        Assert.Equal(enabled && !admin, host.RunEnabled);
    }

    [Fact]
    public void Apply_NewTask_HasInteractiveUserTriggerAndPersistentRuntimeSettings()
    {
        var host = new TaskHost();
        Assert.True(host.Create().Apply(true, true, out var error), error);
        Assert.NotNull(host.CreatedXml);
        var task = XDocument.Parse(host.CreatedXml).Root!;
        Assert.Equal(CurrentSid, task.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.Value);
        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal(CurrentSid, principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal(Environment.ProcessPath, task.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value);
        Assert.False(File.Exists(host.CreatedXmlPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_OwnLegacyTask_AdoptsWithoutCreatingDuplicate(bool admin)
    {
        var host = new TaskHost();
        host.Tasks[LegacyName] = TaskXml(CurrentSid);
        var service = host.Create();
        Assert.True(service.GetState().StartAsAdmin);
        Assert.True(service.Apply(true, admin, out var error), error);
        Assert.All(host.Mutations, name => Assert.Equal(LegacyName, name));
        Assert.Equal(admin, host.Tasks.ContainsKey(LegacyName));
        Assert.False(host.Tasks.ContainsKey($"{LegacyName}_{CurrentSid}"));
    }

    [Fact]
    public void BuildTaskDefinition_PathWithSpacesAndXmlCharacters_PreservesExecutable()
    {
        const string executable = @"C:\Program Files\Tools & Apps\sWinShortcuts.exe";
        var definition = StartupService.BuildTaskDefinition(OtherSid, executable);
        var reparsed = XDocument.Parse(definition.ToString());
        Assert.Equal(executable, reparsed.Root!.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Apply_ForeignTaskInCurrentUserNamespace_FailsWithoutMutation(bool enabled, bool admin)
    {
        var host = new TaskHost();
        host.Tasks[$"{LegacyName}_{CurrentSid}"] = TaskXml(OtherSid);
        Assert.False(host.Create().Apply(enabled, admin, out var error));
        Assert.Contains("principal", error);
        Assert.Empty(host.Mutations);
        Assert.Equal(0, host.RunWrites);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_MalformedTaskXml_FailsWithoutMutation(bool legacy)
    {
        var host = new TaskHost();
        host.Tasks[legacy ? LegacyName : $"{LegacyName}_{CurrentSid}"] = "<Task";
        Assert.False(host.Create().Apply(true, true, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(host.Mutations);
        Assert.Equal(0, host.RunWrites);
    }

    [Fact]
    public void Apply_LegacyGroupPrincipal_DoesNotAdoptOrDeleteTask()
    {
        var host = new TaskHost();
        var groupTask = TaskXml(CurrentSid).Replace("UserId", "GroupId", StringComparison.Ordinal);
        host.Tasks[LegacyName] = groupTask;
        Assert.True(host.Create().Apply(true, true, out var error), error);
        Assert.Equal(groupTask, host.Tasks[LegacyName]);
        Assert.DoesNotContain(LegacyName, host.Mutations);
    }

    [Fact]
    public void Apply_OwnLegacyTaskRegistryFailure_RestoresSameLegacyTask()
    {
        var host = new TaskHost { FailNextWrite = true };
        host.Tasks[LegacyName] = TaskXml(CurrentSid);
        Assert.False(host.Create().Apply(true, false, out var error));
        Assert.Contains("restored", error);
        Assert.True(host.Tasks.ContainsKey(LegacyName));
        Assert.False(host.Tasks.ContainsKey($"{LegacyName}_{CurrentSid}"));
        Assert.False(host.RunEnabled);
    }

    [Fact]
    public void Apply_LegacyAccountName_ResolvesCurrentUserBeforeAdoption()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var host = new TaskHost();
        host.Tasks[LegacyName] = TaskXml(identity.Name);
        Assert.True(host.Create().Apply(false, false, out var error), error);
        Assert.False(host.Tasks.ContainsKey(LegacyName));
        Assert.Equal(LegacyName, Assert.Single(host.Mutations));
    }

    private static string TaskXml(string sid) => new XElement(Ns + "Task",
        new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XElement(Ns + "UserId", sid)))).ToString();

    private sealed class TaskHost
    {
        internal Dictionary<string, string> Tasks { get; } = [];
        internal List<string> Mutations { get; } = [];
        internal bool RunEnabled { get; private set; }
        internal int RunWrites { get; private set; }
        internal bool FailNextWrite { get; set; }
        internal string? CreatedXml { get; private set; }
        internal string? CreatedXmlPath { get; private set; }
        internal StartupService Create() => new(Run, () => RunEnabled, enabled =>
        {
            RunWrites++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new UnauthorizedAccessException("registry unavailable");
            }
            RunEnabled = enabled;
        });

        private bool Run(string arguments, int timeoutMs, out int exitCode, out string stdout, out string stderr)
        {
            var name = Regex.Match(arguments, "/TN \"([^\"]+)\"").Groups[1].Value;
            stdout = stderr = "";
            exitCode = 0;
            if (arguments.StartsWith("/Query "))
            {
                if (Tasks.TryGetValue(name, out var xml)) stdout = xml;
                else exitCode = unchecked((int)0x80070002);
                return true;
            }
            Mutations.Add(name);
            if (arguments.StartsWith("/Delete ")) Tasks.Remove(name);
            else
            {
                var file = Regex.Match(arguments, "/XML \"([^\"]+)\"");
                CreatedXmlPath = file.Success ? file.Groups[1].Value : null;
                CreatedXml = CreatedXmlPath is null ? null : File.ReadAllText(CreatedXmlPath);
                Tasks[name] = CreatedXml ?? TaskXml(CurrentSid);
            }
            return true;
        }
    }
}
