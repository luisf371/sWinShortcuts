using sWinShortcuts.Services;
using System.Security.Principal;
using Xunit;

namespace Tests;

public sealed class StartupServiceTests
{
    private const int Missing = unchecked((int)0x80070002);
    private const int Denied = unchecked((int)0x80070005);
    private static string OwnedTaskXml
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return $"<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals><Principal><UserId>{identity.User!.Value}</UserId></Principal></Principals></Task>";
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Apply_UnknownTaskState_DoesNotMutateStartup(bool enabled, bool admin)
    {
        var host = new FakeStartupHost { QueryExit = Denied };
        var service = host.Create();
        Assert.False(service.Apply(enabled, admin, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(host.Writes);
        Assert.All(host.Commands, args => Assert.StartsWith("/Query ", args));
    }

    [Theory]
    [InlineData(true, 0, "", true)]
    [InlineData(true, Missing, "", false)]
    [InlineData(true, Missing, "Accès refusé", false)]
    public void GetState_UsesCompletedHresult(bool completed, int code, string stderr, bool present)
    {
        var host = new FakeStartupHost { Completed = completed, QueryExit = code, Error = stderr };
        var state = host.Create().GetState();
        Assert.Equal(present, state.StartWithWindows);
        Assert.Equal(present, state.StartAsAdmin);
        Assert.All(host.Commands, args => Assert.Contains("/HRESULT", args));
        Assert.All(host.Timeouts, timeout => Assert.Equal(3000, timeout));
    }

    [Theory]
    [InlineData(false, 0, "")]
    [InlineData(false, Missing, "")]
    [InlineData(true, Denied, "")]
    [InlineData(true, Denied, "The system cannot find the file specified.")]
    [InlineData(true, 1, "not found")]
    [InlineData(true, -1, "Accès refusé")]
    public void GetState_UnreadableTaskState_Throws(bool completed, int code, string error)
    {
        var host = new FakeStartupHost { Completed = completed, QueryExit = code, Error = error, RunEnabled = true };
        Assert.Throws<InvalidOperationException>(() => host.Create().GetState());
        Assert.Empty(host.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Apply_QueryTimeout_DoesNotMutateStartup(bool enabled, bool admin)
    {
        var host = new FakeStartupHost { Completed = false };
        Assert.False(host.Create().Apply(enabled, admin, out var error));
        Assert.Contains("Timed out", error);
        Assert.Empty(host.Writes);
        Assert.All(host.Commands, args => Assert.StartsWith("/Query ", args));
    }

    [Fact]
    public void QueryRunnerException_IsReportedWithoutMutation()
    {
        var host = new FakeStartupHost { QueryThrows = true };
        var service = host.Create();
        Assert.Throws<InvalidOperationException>(() => service.GetState());
        Assert.False(service.Apply(false, false, out var error));
        Assert.Contains("query unavailable", error);
        Assert.Empty(host.Writes);
    }

    [Fact]
    public void Disable_ConfirmedAbsence_IsSuccessfulWithoutDelete()
    {
        var host = new FakeStartupHost { QueryExit = Missing };
        Assert.True(host.Create().Apply(false, false, out var error));
        Assert.Null(error);
        Assert.Equal(new[] { false }, host.Writes);
        Assert.All(host.Commands, args => Assert.StartsWith("/Query ", args));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Apply_DeleteFails_DoesNotProceedToRegistryOrCreate(bool enabled, bool admin)
    {
        var host = new FakeStartupHost { QueryExit = 0, DeleteExit = 5 };
        Assert.False(host.Create().Apply(enabled, admin, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(host.Writes);
        Assert.DoesNotContain(host.Commands, args => args.StartsWith("/Create "));
    }

    [Fact]
    public void DowngradeToNormal_DeletesTaskBeforeEnablingRunKey()
    {
        var host = new FakeStartupHost { QueryExit = 0 };
        Assert.True(host.Create().Apply(true, false, out _));
        Assert.Equal(new[] { "query", "delete", "enable" }, host.Events);
    }

    [Fact]
    public void EnableAdmin_CreatesTaskBeforeRemovingRunKey()
    {
        var host = new FakeStartupHost { QueryExit = Missing };
        Assert.True(host.Create().Apply(true, true, out _));
        Assert.Equal(new[] { "query", "query", "create", "disable" }, host.Events);
    }

    [Fact]
    public void RegistryReadFailure_IsNotReportedAsDisabled()
    {
        var host = new FakeStartupHost { QueryExit = Missing, ReadThrows = true };
        Assert.Throws<UnauthorizedAccessException>(() => host.Create().GetState());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistryWriteFailure_IsReported(bool enabled)
    {
        var host = new FakeStartupHost { QueryExit = Missing, WriteThrows = true };
        Assert.False(host.Create().Apply(enabled, false, out var error));
        Assert.Contains("registry unavailable", error);
    }

    [Fact]
    public void Apply_AdminReplacementFails_PreservesExistingTask()
    {
        var host = new MutableStartupHost { TaskPresent = true, FailCreate = true };
        Assert.False(host.Create().Apply(true, true, out var error));
        Assert.True(host.TaskPresent);
        Assert.False(host.RunEnabled);
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("/Delete "));
        Assert.Contains("create failed", error);
    }

    [Fact]
    public void Apply_NormalStartupWriteFails_RestoresPreviousTask()
    {
        var host = new MutableStartupHost { TaskPresent = true, FailNextWrite = true };
        Assert.False(host.Create().Apply(true, false, out var error));
        Assert.True(host.TaskPresent);
        Assert.False(host.RunEnabled);
        Assert.Contains("restored", error);
    }

    [Fact]
    public void Apply_CompensationFails_ReportsFailureAndActualState()
    {
        var host = new MutableStartupHost { TaskPresent = true, FailNextWrite = true, FailCreate = true };
        Assert.False(host.Create().Apply(true, false, out var error));
        Assert.False(host.TaskPresent);
        Assert.False(host.RunEnabled);
        Assert.Contains("restoration failed", error);
        Assert.Contains("disabled", error);
    }

    [Fact]
    public void Apply_AdminRunKeyRemovalFails_RestoresNormalStartup()
    {
        var host = new MutableStartupHost { RunEnabled = true, FailNextWrite = true };
        Assert.False(host.Create().Apply(true, true, out var error));
        Assert.False(host.TaskPresent);
        Assert.True(host.RunEnabled);
        Assert.Contains("restored", error);
    }

    [Fact]
    public void Apply_RegistryWriteMutatesThenThrows_RestoresPreviousState()
    {
        var host = new MutableStartupHost { FailNextWrite = true, MutateBeforeFailure = true };
        Assert.False(host.Create().Apply(true, false, out var error));
        Assert.False(host.TaskPresent);
        Assert.False(host.RunEnabled);
        Assert.Contains("restored", error);
    }

    [Fact]
    public void Apply_RegistryStateUnknown_DoesNotMutateTask()
    {
        var host = new FakeStartupHost { QueryExit = 0, ReadThrows = true };
        Assert.False(host.Create().Apply(true, false, out _));
        Assert.All(host.Commands, command => Assert.StartsWith("/Query ", command));
        Assert.Empty(host.Writes);
    }

    private sealed class MutableStartupHost
    {
        internal bool TaskPresent { get; set; }
        internal bool RunEnabled { get; set; }
        internal bool FailCreate { get; init; }
        internal bool FailNextWrite { get; set; }
        internal bool MutateBeforeFailure { get; init; }
        internal List<string> Commands { get; } = [];

        internal StartupService Create() => new(Run, () => RunEnabled, enabled =>
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                if (MutateBeforeFailure) RunEnabled = enabled;
                throw new UnauthorizedAccessException("registry unavailable");
            }
            RunEnabled = enabled;
        });

        private bool Run(string arguments, int timeoutMs, out int exitCode, out string stdout, out string stderr)
        {
            Commands.Add(arguments);
            stdout = stderr = "";
            exitCode = 0;
            if (arguments.StartsWith("/Query "))
            {
                exitCode = TaskPresent ? 0 : Missing;
                stdout = TaskPresent ? OwnedTaskXml : "";
            }
            else if (arguments.StartsWith("/Delete ")) TaskPresent = false;
            else if (FailCreate)
            {
                exitCode = Denied;
                stderr = "create failed";
            }
            else TaskPresent = true;
            return true;
        }
    }

    private sealed class FakeStartupHost
    {
        internal int QueryExit { get; init; } = Missing;
        internal int DeleteExit { get; init; }
        internal bool Completed { get; init; } = true;
        internal string Error { get; init; } = "";
        internal bool RunEnabled { get; init; }
        internal bool QueryThrows { get; init; }
        internal bool ReadThrows { get; init; }
        internal bool WriteThrows { get; init; }
        internal List<string> Commands { get; } = [];
        internal List<int> Timeouts { get; } = [];
        internal List<bool> Writes { get; } = [];
        internal List<string> Events { get; } = [];

        internal StartupService Create() => new(Run,
            () => ReadThrows ? throw new UnauthorizedAccessException("registry unavailable") : RunEnabled,
            enabled =>
            {
                if (WriteThrows) throw new UnauthorizedAccessException("registry unavailable");
                Writes.Add(enabled);
                Events.Add(enabled ? "enable" : "disable");
            });

        private bool Run(string arguments, int timeoutMs, out int exitCode, out string stdout, out string stderr)
        {
            Commands.Add(arguments);
            Timeouts.Add(timeoutMs);
            stdout = "";
            stderr = Error;
            if (arguments.StartsWith("/Query "))
            {
                Events.Add("query");
                if (QueryThrows) throw new InvalidOperationException("query unavailable");
                exitCode = QueryExit;
                stdout = QueryExit == 0 ? OwnedTaskXml : "";
                return Completed;
            }
            if (arguments.StartsWith("/Delete "))
            {
                Events.Add("delete");
                exitCode = DeleteExit;
                return true;
            }
            Events.Add("create");
            exitCode = 0;
            return true;
        }
    }
}
