using System.Text.Json;
using Wolfden.Index;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>wolfden index --watch</c>: keeping the index up to date while the
/// writer edits notes (#14). It runs until stopped, so each test runs it on
/// another thread and stops it as Ctrl+C would. File events take a moment,
/// so the tests wait generously.
/// </summary>
public class IndexWatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const string Mina = "universes/dracula/books/dracula/entities/characters/mina-harker.md";

    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void ItRebuildsThenPrintsALineForEachChangeUntilStopped()
    {
        using var den = TempDen.DraculaCopy();
        using var run = new Watch("index", "--watch", den.Root);
        run.WaitForOutput("Watching for changes.");

        den.Write(Mina, den.Read(Mina).Replace("name: Mina Harker", "name: Mina Murray Harker", StringComparison.Ordinal));
        run.WaitForOutput($"Updated {Mina}");
        using (var index = DenIndex.Open(den.Root))
        {
            Assert.Equal("Mina Murray Harker", index.FindEntity(Mina)!.Name);
        }

        var (exitCode, output, error) = run.Stop();

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches(@"^\d\d:\d\d:\d\d Rebuilt \.wolfden/index\.db \(the watcher started\) in [\d.]+ m?s: 1 universe, 50 entities, 372 links; 0 errors, \d+ warnings?\.$", lines[0]);
        Assert.Equal("Watching for changes. Press Ctrl+C to stop.", lines[1]);
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, $@"^\d\d:\d\d:\d\d Updated {Mina} in [\d.]+ m?s: 1 universe, 50 entities, 372 links; "));
        Assert.Equal("Stopped watching.", lines[^1]);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void JsonPrintsOneObjectPerLine()
    {
        using var den = TempDen.DraculaCopy();
        using var run = new Watch("index", "--watch", "--json", "--den", den.Root);
        run.WaitForOutput("\"event\":\"rebuilt\"");

        den.Write(Mina, den.Read(Mina) + "\nOne more line.\n");
        run.WaitForOutput("\"event\":\"updated\"");
        var (exitCode, output, _) = run.Stop();

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.Equal(1, JsonDocument.Parse(l).RootElement.GetProperty("version").GetInt32()));
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(
            ["version", "event", "time", "reason", "changedFiles", "rows", "milliseconds", "denLoaded", "files", "universes", "entities", "links", "errors", "warnings"],
            first.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("the watcher started", first.RootElement.GetProperty("reason").GetString());
        using var update = JsonDocument.Parse(lines.First(l => l.Contains("\"updated\"", StringComparison.Ordinal)));
        Assert.Equal([Mina], update.RootElement.GetProperty("changedFiles").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(JsonValueKind.Null, update.RootElement.GetProperty("reason").ValueKind);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-5")]
    public void ASecondWatcherOnTheSameDenExits2()
    {
        using var den = TempDen.DraculaCopy();
        using var run = new Watch("index", "--watch", den.Root);
        run.WaitForOutput("Watching for changes.");

        var (exitCode, output, error) = Cli.Run("index", "--watch", den.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Contains("already watching", error, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitOk, run.Stop().ExitCode);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-3")]
    public void ItKeepsWatchingWhileDenYamlIsBrokenSoItCanBeFixed()
    {
        using var den = TempDen.DraculaCopy();
        var denYaml = den.Read("den.yaml");
        den.Write("den.yaml", "format: [broken\n");
        using var run = new Watch("index", "--watch", den.Root);
        run.WaitForOutput("den.yaml doesn't load, so nothing is indexed");

        den.Write("den.yaml", denYaml);
        run.WaitForOutput("Rebuilt .wolfden/index.db (den.yaml changed)");
        var (exitCode, output, _) = run.Stop();

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("1 universe, 50 entities, 372 links", output, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpDescribesWatch()
    {
        var (exitCode, output, _) = Cli.Run("index", "--help");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("--watch", output, StringComparison.Ordinal);
        Assert.Contains("usage: wolfden index [<den>] [--den <den>] [--watch] [--json]", output, StringComparison.Ordinal);
    }

    /// <summary><c>wolfden</c> running on another thread until <see cref="Stop"/>, as if until Ctrl+C.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly StringWriter _output = new();
        private readonly StringWriter _error = new();
        private readonly TextWriter _syncOutput;
        private readonly TextWriter _syncError;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<int> _run;

        public Watch(params string[] args)
        {
            _syncOutput = TextWriter.Synchronized(_output);
            _syncError = TextWriter.Synchronized(_error);
            _run = Task.Run(() => CliApp.RunUntil(args, _syncOutput, _syncError, _stop.Token), TestContext.Current.CancellationToken);
        }

        public string Output
        {
            get
            {
                lock (_syncOutput)
                {
                    return _output.ToString().ReplaceLineEndings("\n");
                }
            }
        }

        public void WaitForOutput(string text)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (!Output.Contains(text, StringComparison.Ordinal))
            {
                if (_run.IsCompleted)
                {
                    Assert.Fail($"wolfden stopped before printing '{text}'. Output:\n{Output}\nError:\n{_error}");
                }

                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"wolfden didn't print '{text}' within {Timeout.TotalSeconds} s. Output:\n{Output}");
                }

                Thread.Sleep(50);
            }
        }

        public (int ExitCode, string Output, string Error) Stop()
        {
            _stop.Cancel();
            Assert.True(_run.Wait(Timeout, TestContext.Current.CancellationToken), "wolfden didn't stop.");
            return (_run.Result, Output, _error.ToString().ReplaceLineEndings("\n"));
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _run.Wait(Timeout);
            }
            catch (AggregateException)
            {
                // Reported by the test that ran it.
            }

            _stop.Dispose();
        }
    }
}
