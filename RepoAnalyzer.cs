using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;


public sealed record LizardTotals(
    int TotalNloc,
    double AvgNloc,
    double AvgCcn,
    double AvgToken,
    int FunctionCount,
    int WarningCount,
    string RepoName
);


public static class RepoAnalyzer
{
    private const int MaxParallelismCap = 16;
    private static readonly TimeSpan ExternalProcessTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Clone -> run lizard -> parse totals -> delete folder.
    /// </summary>
    public static async Task<LizardTotals> CloneAnalyzeDeleteAsync(
        GitHubRepoItem repo,
        CancellationToken ct = default)
    {
        var fullName = !string.IsNullOrWhiteSpace(repo.FullName)
            ? repo.FullName
            : throw new ArgumentException("Repository full name cannot be null or empty.", nameof(repo));

        var tempRoot = Path.Combine(Path.GetTempPath(), "repo-analysis");
        Directory.CreateDirectory(tempRoot);

        // Use a unique folder per run
        var safeName = fullName.Replace('/', '_');
        var workDir = Path.Combine(tempRoot, $"{safeName}_{Guid.NewGuid():N}");

        try
        {
            Console.WriteLine($"Analyzing {fullName}...");
            // 1) Clone (shallow)
            var cloneUrl = $"https://github.com/{fullName}.git";
            await RunProcessAsync(
                fileName: "git",
                arguments: $"clone --depth 1 {EscapeArg(cloneUrl)} {EscapeArg(workDir)}",
                workingDirectory: tempRoot,
                ct: ct);

            // Optional: remove .git to shrink disk usage before analysis
            var gitDir = Path.Combine(workDir, ".git");
            if (Directory.Exists(gitDir))
                TryDeleteDirectory(gitDir);

            // 2) Analyze with lizard (recursive by default)
            // You can exclude tests if you want: -x"**/test/**" etc. :contentReference[oaicite:1]{index=1}
            var lizardOutput = await RunProcessCaptureStdoutAsync(
                fileName: "/Users/runescape/Library/Python/3.12/bin/lizard",
                arguments: EscapeArg(workDir),
                workingDirectory: tempRoot,
                ct: ct);

            // 3) Parse the totals summary
            var totals = ParseLizardTotals(lizardOutput, fullName);
            
            return totals;
        }
        finally
        {
            // 4) Always delete the repo folder
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// Analyze repositories concurrently with bounded parallelism.
    /// Results preserve the input order.
    /// </summary>
    public static async Task<IReadOnlyList<LizardTotals>> CloneAnalyzeDeleteManyAsync(
        IEnumerable<GitHubRepoItem> repos,
        int? maxDegreeOfParallelism = null,
        CancellationToken ct = default)
    {
        var repoList = repos.ToList();
        if (repoList.Count == 0)
            return Array.Empty<LizardTotals>();

        var degree = maxDegreeOfParallelism.GetValueOrDefault(GetDefaultParallelism());
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "Parallelism must be at least 1.");

        var results = new ConcurrentDictionary<int, LizardTotals>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, repoList.Count), options, async (index, token) =>
        {
            var totals = await CloneAnalyzeDeleteAsync(repoList[index], token);
            results[index] = totals;
        });

        return Enumerable.Range(0, repoList.Count)
            .Select(i => results[i])
            .ToList();
    }

    /// <summary>
    /// Analyze repositories concurrently and invoke callbacks for each completion.
    /// Continues processing when a single repo fails.
    /// </summary>
    public static async Task CloneAnalyzeDeleteManyWithCallbacksAsync(
        IEnumerable<GitHubRepoItem> repos,
        Func<LizardTotals, Task> onSuccess,
        Func<GitHubRepoItem, Exception, Task>? onError = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repos);
        ArgumentNullException.ThrowIfNull(onSuccess);

        var repoList = repos.ToList();
        if (repoList.Count == 0)
            return;

        var degree = maxDegreeOfParallelism.GetValueOrDefault(GetDefaultParallelism());
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "Parallelism must be at least 1.");

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(repoList, options, async (repo, token) =>
        {
            try
            {
                var totals = await CloneAnalyzeDeleteAsync(repo, token);
                await onSuccess(totals);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (onError is not null)
                    await onError(repo, ex);
            }
        });
    }

    private static LizardTotals ParseLizardTotals(string output, string repoName)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        int headerIdx = Array.FindIndex
            (
                lines, l =>
                l.Contains("Total NLOC", StringComparison.OrdinalIgnoreCase) &&
                l.Contains("Avg", StringComparison.OrdinalIgnoreCase) &&
                l.Contains("Warning", StringComparison.OrdinalIgnoreCase)
            );

        if (headerIdx < 0 || headerIdx + 2 >= lines.Length)
        {
            throw new InvalidOperationException("Could not locate Lizard totals header in output.");
        }

        // Find the next line that starts with a number (skip separators)
        var dataLine = lines.Skip(headerIdx + 1).FirstOrDefault(l => Regex.IsMatch(l, @"^\s*\d+"));
        if (dataLine is null)
        {
            throw new InvalidOperationException("Could not locate Lizard totals data row in output.");
        }

        // Split on whitespace
        var parts = Regex.Split(dataLine.Trim(), @"\s+");

        if (parts.Length < 6)
        {
            throw new InvalidOperationException($"Unexpected totals row format: '{dataLine}'");
        }

        return new LizardTotals
        (
            TotalNloc: int.Parse(parts[0]),
            AvgNloc: double.Parse(parts[1]),
            AvgCcn: double.Parse(parts[2]),
            AvgToken: double.Parse(parts[3]),
            FunctionCount: int.Parse(parts[4]),
            WarningCount: int.Parse(parts[5]),
            RepoName: repoName
        );
    }

    private static async Task RunProcessAsync
    (
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct
    )
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        p.Start();

        // Drain output to avoid deadlocks on large stderr/stdout
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var timedOut = await WaitForExitWithTimeoutAsync(p, fileName, ct);
        if (timedOut)
        {
            var timeoutStdout = await stdoutTask;
            var timeoutStderr = await stderrTask;
            throw new TimeoutException(
                $"{fileName} timed out after {ExternalProcessTimeout.TotalSeconds:0}s.\n" +
                $"STDOUT:\n{timeoutStdout}\nSTDERR:\n{timeoutStderr}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (p.ExitCode != 0)
        {
            throw new Exception($"{fileName} exited {p.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
    }
    private static async Task<string> RunProcessCaptureStdoutAsync
    (
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct
    )
    {
        // On macOS, PATH from IDE-launched processes may not include brew/pip paths.
        // Running via zsh -lc uses your login shell environment so commands like `lizard` resolve.
        var isMac = OperatingSystem.IsMacOS();
        var isLinux = OperatingSystem.IsLinux();

        ProcessStartInfo psi;

        if (isMac || isLinux)
        {
            // Execute in a login shell so PATH is correct
            psi = new ProcessStartInfo
            {
                FileName = "/bin/zsh",          // macOS default shell; works well for brew env
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // -l = login shell, -c = command string
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add($"{fileName} {arguments}");
        }
        else
        {
            // Windows path (keep your existing behavior)
            psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        using var p = new Process { StartInfo = psi };

        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Provide a much more actionable error message
            throw new Exception(
                $"Failed to start process '{psi.FileName}'. " +
                $"Likely PATH issue (e.g., '{fileName}' not found). " +
                $"Try running `which {fileName}` in Terminal and ensure the same PATH is available to your app. " +
                $"Original: {ex.Message}",
                ex);
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var timedOut = await WaitForExitWithTimeoutAsync(p, fileName, ct);
        if (timedOut)
        {
            var timeoutStdout = await stdoutTask;
            var timeoutStderr = await stderrTask;
            throw new TimeoutException(
                $"{fileName} timed out after {ExternalProcessTimeout.TotalSeconds:0}s.\n" +
                $"STDOUT:\n{timeoutStdout}\nSTDERR:\n{timeoutStderr}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Lizard may exit non-zero due to warning thresholds but still produce output.
        if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new Exception($"{fileName} exited {p.ExitCode}\nSTDERR:\n{stderr}");

        return stdout;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            // Clear read-only attributes if any
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attr = File.GetAttributes(file);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
                }
                catch { /* ignore */ }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; for production, log this.
        }
    }

    private static string EscapeArg(string arg)
        => OperatingSystem.IsWindows() ? $"\"{arg}\"" : arg;

    private static async Task<bool> WaitForExitWithTimeoutAsync(
        Process process,
        string fileName,
        CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(ExternalProcessTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            return false;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw new OperationCanceledException($"{fileName} canceled.", ct);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort
        }
    }

    private static int GetDefaultParallelism()
    {
        // Cloning and process execution are mostly I/O bound. Keep this bounded
        // to avoid overwhelming disk/network while still using concurrency.
        var calculated = Math.Max(2, Environment.ProcessorCount * 2);
        return Math.Min(calculated, MaxParallelismCap);
    }
}
