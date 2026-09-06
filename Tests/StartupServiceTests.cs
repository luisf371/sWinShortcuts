using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class StartupServiceTests
{
    private const int Missing = unchecked((int)0x80070002);
    private const int Denied = unchecked((int)0x80070005);

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
        Assert.Contains("/HRESULT", Assert.Single(host.Commands));
        Assert.Equal(3000, Assert.Single(host.Timeouts));
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
        Assert.StartsWith("/Query ", Assert.Single(host.Commands));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
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
        Assert.Equal(new[] { "query", "create", "disable" }, host.Events);
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
