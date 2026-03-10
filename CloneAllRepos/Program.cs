List<string> fails = [];

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

try
{
    IConfiguration config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", true, true)
        .AddUserSecrets(typeof(Program).Assembly)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var appConfig = config.Get<AppConfig>() ?? throw new Exception("Failed to bind appsettings.json to AppConfig");

    var targetDirectory = appConfig.TargetDirectory;
    if (targetDirectory is not { Length: > 0 })
    {
        throw new Exception($"{nameof(targetDirectory)} is not set");
    }

    var logPath = Path.Combine(targetDirectory, "log_.txt");

    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(
            logPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 1)
        .CreateLogger();

    var githubUserName = appConfig.GithubUserName;
    if (githubUserName is not { Length: > 0 })
    {
        throw new Exception($"{nameof(githubUserName)} is not set");
    }

    var authCheck = Process.Start(new ProcessStartInfo
    {
        FileName = "gh", Arguments = "auth status", RedirectStandardError = false, CreateNoWindow = true
    }) ?? throw new Exception("Cannot start gh process");

    const int authCheckTimeoutMilliseconds = 30000;
    var exited = authCheck.WaitForExit(authCheckTimeoutMilliseconds);
    if (!exited)
    {
        try
        {
            authCheck.Kill();
        }
        catch
        {
            // Ignore any exceptions when attempting to kill a hung process.
        }

        throw new Exception("Timed out waiting for 'gh auth status'.");
    }

    if (authCheck.ExitCode != 0)
    {
        throw new Exception("gh CLI is not authenticated. Run 'gh auth login' first.");
    }

    var ownersToInclude = appConfig.OwnersToInclude;
    if (ownersToInclude is not { Count: > 0 })
    {
        ownersToInclude = [githubUserName];
    }

    List<GitHubRepo> repos = [];
    foreach (var owner in ownersToInclude)
    {
        const int repoLimit = 1000;
        var listProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = $"repo list {owner} --json name,owner,isFork,isArchived,sshUrl --limit {repoLimit}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new Exception("Cannot start gh process");

        const int repoListTimeoutMs = 60_000;
        var stdoutTask = listProcess.StandardOutput.ReadToEndAsync();
        var stderrTask = listProcess.StandardError.ReadToEndAsync();
        var completionTask = Task.WhenAll(stdoutTask, stderrTask);
        var timeoutTask = Task.Delay(repoListTimeoutMs);

        if (await Task.WhenAny(completionTask, timeoutTask) == timeoutTask)
        {
            try
            {
                if (!listProcess.HasExited)
                {
                    listProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to kill timed-out gh process for owner {Owner}", owner);
            }

            fails.Add($"Owner '{owner}': gh repo list timed out after {repoListTimeoutMs} ms.");
            Log.Error("gh repo list for owner {Owner} timed out after {TimeoutMs} ms.", owner, repoListTimeoutMs);
            continue;
        }

        var json = await stdoutTask;
        var repoListStderr = await stderrTask;
        listProcess.WaitForExit();

        if (listProcess.ExitCode != 0)
        {
            fails.Add($"Owner '{owner}': gh repo list failed with exit code {listProcess.ExitCode}.");
            Log.Error("gh repo list for owner {Owner} failed with exit code {ExitCode}. stderr: {Stderr}",
                owner, listProcess.ExitCode, repoListStderr);
            continue;
        }

        var ownerRepos = JsonSerializer.Deserialize<List<GitHubRepo>>(json, jsonOptions) ?? [];
        repos.AddRange(ownerRepos.Where(r => !r.IsArchived));
    }

    foreach (var repo in repos.Where(r => r.IsFork))
    {
        await SyncForkAsync(repo, appConfig.ForceSyncRepos);
    }

    var allDirs = Directory.GetDirectories(targetDirectory).Select(dir => new DirectoryInfo(dir).Name);
    var repoDirs = repos.Select(repo => repo.Name);

    var remainingDirs = allDirs.Where(dir => !repoDirs.Contains(dir, StringComparer.OrdinalIgnoreCase));

    foreach (var repo in repos)
    {
        CloneOrUpdateRepo(targetDirectory, repo);
    }

    foreach (var repo in remainingDirs)
    {
        var destinationPath = Path.Combine(targetDirectory, repo);
        if (!Directory.Exists(Path.Combine(destinationPath, ".git")))
        {
            continue;
        }

        PullRepo(destinationPath, repo);
    }
}
catch (Exception ex)
{
    LogExceptions(ex);
}
finally
{
    if (fails.Count > 0)
    {
        Log.Warning("Failures occurred:");
        foreach (var fail in fails)
        {
            Log.Warning("{FailureDetails}", fail);
        }
    }

    Log.CloseAndFlush();
}

async Task SyncForkAsync(GitHubRepo repo, List<string> forceSyncRepos)
{
    var repoRef = $"{repo.Owner.Login}/{repo.Name}";
    var useForce =
        forceSyncRepos.Contains(repoRef, StringComparer.OrdinalIgnoreCase) ||
        forceSyncRepos.Contains(repo.Name, StringComparer.OrdinalIgnoreCase);
    var forceFlag = useForce
        ? " --force"
        : string.Empty;

    const int maxAttempts = 3;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            Log.Information("Syncing fork {RepoName} (attempt {Attempt}/{Max})", repo.Name, attempt, maxAttempts);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"repo sync{forceFlag} {repoRef}",
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new Exception("Cannot start gh process");

            var stderrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* Ignore */ }
                var _ = await stderrTask;
                Log.Warning("Syncing fork '{RepoName}' timed out on attempt {Attempt}/{Max}",
                    repo.Name, attempt, maxAttempts);
                if (attempt < maxAttempts)
                {
                    var delay = (int)Math.Pow(2, attempt) * 1000;
                    await Task.Delay(delay);
                    continue;
                }

                fails.Add($"{repo.Name}: timed out syncing fork after {maxAttempts} attempts");
                return;
            }

            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                Log.Information("Fork {RepoName} synced successfully", repo.Name);
                return;
            }

            if (IsPermanentError(stderr))
            {
                Log.Error("Fork '{RepoName}' cannot be synced: {Error}", repo.Name, stderr.Trim());
                fails.Add($"{repo.Name}: {stderr.Trim()}");
                return;
            }

            if (IsDivergedError(stderr))
            {
                Log.Warning(
                    "Fork '{RepoName}' has diverged from upstream and cannot be synced automatically. " +
                    "To force-sync (discarding local changes): gh repo sync --force {RepoRef}",
                    repo.Name, repoRef);
                fails.Add($"{repo.Name}: fork has diverged (gh repo sync --force {repoRef})");
                return;
            }

            // Transient failure — retry with backoff
            if (attempt < maxAttempts)
            {
                var delay = (int)Math.Pow(2, attempt) * 1000;
                Log.Warning("Transient error syncing fork '{RepoName}', retrying in {Delay}ms: {Error}",
                    repo.Name, delay, stderr.Trim());
                await Task.Delay(delay);
            }
            else
            {
                Log.Error("Fork '{RepoName}' failed to sync after {Max} attempts: {Error}",
                    repo.Name, maxAttempts, stderr.Trim());
                fails.Add($"{repo.Name}: {stderr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            LogExceptions(ex, repo.Name);
            return;
        }
    }
}

bool IsPermanentError(string stderr) =>
    stderr.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
    stderr.Contains("not have permission", StringComparison.OrdinalIgnoreCase) ||
    stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase);

bool IsDivergedError(string stderr) =>
    stderr.Contains("diverged", StringComparison.OrdinalIgnoreCase) ||
    stderr.Contains("not possible to fast-forward", StringComparison.OrdinalIgnoreCase) ||
    stderr.Contains("not a fast-forward", StringComparison.OrdinalIgnoreCase);

void CloneOrUpdateRepo(string targetReposDirectory, GitHubRepo repo)
{
    try
    {
        var destinationPath = Path.Combine(targetReposDirectory, repo.Name);
        if (!Directory.Exists(destinationPath))
        {
            Log.Information("Cloning {RepoName}", repo.Name);
            var process =
                Process.Start(new ProcessStartInfo
                {
                    WorkingDirectory = targetReposDirectory,
                    FileName = "git",
                    Arguments = $"clone {repo.SshUrl} --no-tags",
                    CreateNoWindow = true
                }) ?? throw new Exception("Cannot create process");

            Log.Information(process.WaitForExit(1000 * 30)
                ? "Repo {RepoName} finished cloning"
                : "Repo {RepoName} did not finish cloning", repo.Name);

            var path = Path.Combine(targetReposDirectory, repo.Name);
            if (!Directory.Exists(path))
            {
                fails.Add($"{repo.Name}: {repo.SshUrl} ({path})");
            }
        }
        else
        {
            Log.Information("Updating {RepoName}", repo.Name);
            PullRepo(destinationPath, repo.Name);
        }
    }
    catch (Exception ex)
    {
        LogExceptions(ex, repo.Name);
    }
}

void LogExceptions(Exception ex, string? repoName = null)
{
    if (repoName is { Length: > 0 })
    {
        Log.Error(ex, "Error with {RepoName}", repoName);
    }
    else
    {
        Log.Error(ex, "Error occurred");
    }

    while (true)
    {
        var errorLine = repoName is { Length: > 0 }
            ? $"{repoName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}"
            : $"{ex.Message}{Environment.NewLine}{ex.StackTrace}";
        fails.Add(errorLine);
        if (ex.InnerException is not null)
        {
            ex = ex.InnerException;
            continue;
        }

        break;
    }
}

void PullRepo(string workingDirectory, string repoName)
{
    var processStartInfo = new ProcessStartInfo
    {
        WorkingDirectory = workingDirectory, FileName = "git", Arguments = "pull", CreateNoWindow = false
    };

    Log.Information("Starting pull for {RepoName}", repoName);
    Process.Start(processStartInfo)?.WaitForExit(1000 * 30);
    Log.Information("Ending pull for {RepoName}", repoName);
}
