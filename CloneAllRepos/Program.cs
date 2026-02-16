List<string> fails = [];

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

    var personalAccessToken = appConfig.PersonalAccessToken;
    if (personalAccessToken is not { Length: > 0 })
    {
        throw new Exception($"{nameof(personalAccessToken)} is not set");
    }

    var ownersToInclude = appConfig.OwnersToInclude;
    if (ownersToInclude is not { Count: > 0 })
    {
        ownersToInclude = [githubUserName];
    }

    var myUser = new ProductHeaderValue(githubUserName);
    var credentials = new Credentials(personalAccessToken);
    var client = new GitHubClient(myUser) { Credentials = credentials };

    var repos = (await client.Repository.GetAllForCurrent())
        .Where(repo => !repo.Archived && ownersToInclude.Contains(repo.Owner.Login, StringComparer.OrdinalIgnoreCase))
        .ToList();

    foreach (var repo in repos.Where(r => r.Fork))
    {
        try
        {
            var fork = await client.Repository.Get(githubUserName, repo.Name);
            var upstream = fork.Parent;
            var compareResult = await client.Repository.Commit.Compare(upstream.Owner.Login, upstream.Name,
                upstream.DefaultBranch, $"{fork.Owner.Login}:{fork.DefaultBranch}");
            if (compareResult.BehindBy > 0)
            {
                Log.Information("Updating fork of {RepoName}", repo.Name);
                var upstreamBranchReference = await client.Git.Reference
                    .Get(upstream.Owner.Login, upstream.Name, $"heads/{upstream.DefaultBranch}");
                await client.Git.Reference.Update(fork.Owner.Login, fork.Name, $"heads/{fork.DefaultBranch}",
                    new ReferenceUpdate(upstreamBranchReference.Object.Sha));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error with fork '{RepoName}'", repo.Name);
            LogExceptions(ex, repo.Name);
        }
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

void CloneOrUpdateRepo(string targetReposDirectory, Repository repo)
{
    try
    {
        var destinationPath = Path.Combine(targetReposDirectory, repo.Name);
        if (!Directory.Exists(destinationPath))
        {
            Log.Information("Cloning {RepoName}", repo.Name);
            var process = Process.Start(new ProcessStartInfo { WorkingDirectory = targetReposDirectory, FileName = "git", Arguments = $"clone {repo.SshUrl} --no-tags", CreateNoWindow = true }) ?? throw new Exception("Cannot create process");

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
    var processStartInfo = new ProcessStartInfo { WorkingDirectory = workingDirectory, FileName = "git", Arguments = "pull", CreateNoWindow = false };

    Log.Information("Starting pull for {RepoName}", repoName);
    Process.Start(processStartInfo)?.WaitForExit(1000 * 30);
    Log.Information("Ending pull for {RepoName}", repoName);
}
