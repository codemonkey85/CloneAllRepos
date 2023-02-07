﻿IList<string> fails = new List<string>();
string? targetDirectory = null;

try
{
    IConfiguration config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", true, true)
        .AddUserSecrets(typeof(Program).Assembly)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    targetDirectory = config.GetValue(nameof(targetDirectory), string.Empty);
    if (targetDirectory is not { Length: > 0 })
    {
        throw new Exception($"{nameof(targetDirectory)} is not set");
    }

    string? githubUserName = config.GetValue(nameof(githubUserName), string.Empty);
    if (githubUserName is not { Length: > 0 })
    {
        throw new Exception($"{nameof(githubUserName)} is not set");
    }

    string? personalAccessToken = config.GetValue(nameof(personalAccessToken), string.Empty);
    if (personalAccessToken is not { Length: > 0 })
    {
        throw new Exception($"{nameof(personalAccessToken)} is not set");
    }

    var myUser = new ProductHeaderValue(githubUserName);
    var credentials = new Credentials(personalAccessToken);
    var client = new GitHubClient(myUser) { Credentials = credentials };

    var repos = (await client.Repository.GetAllForCurrent())
        .Where(r => !r.Archived)
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
                WriteLine($"Updating fork of {repo.Name}");
                var upstreamBranchReference = await client.Git.Reference
                    .Get(upstream.Owner.Login, upstream.Name, $"heads/{upstream.DefaultBranch}");
                await client.Git.Reference.Update(fork.Owner.Login, fork.Name, $"heads/{fork.DefaultBranch}",
                    new ReferenceUpdate(upstreamBranchReference.Object.Sha));
            }
        }
        catch (Exception ex)
        {
            WriteLine($"Error with fork '{repo.Name}':");
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
    if (fails.Count > 0 && targetDirectory is { Length: > 0 })
    {
        var sbFails = new StringBuilder();
        sbFails.AppendLine($"{Environment.NewLine}Fails:{Environment.NewLine}");
        foreach (var fail in fails)
        {
            sbFails.AppendLine(fail);
        }

        WriteLine(sbFails);
        File.WriteAllText(Path.Combine(targetDirectory, "log.txt"), sbFails.ToString());
    }
}

void CloneOrUpdateRepo(string targetReposDirectory, Repository repo)
{
    try
    {
        var destinationPath = Path.Combine(targetReposDirectory, repo.Name);
        if (!Directory.Exists(destinationPath))
        {
            WriteLine($"Cloning {repo.Name}");
            var process = Process.Start(new ProcessStartInfo
            {
                WorkingDirectory = targetReposDirectory,
                FileName = "git",
                Arguments = $"clone {repo.SshUrl} --no-tags",
                CreateNoWindow = true
            });
            if (process is null)
            {
                throw new Exception("Cannot create process");
            }

            WriteLine(process.WaitForExit(1000 * 30)
                ? $"Repo {repo.Name} finished cloning"
                : $"Repo {repo.Name} did not finish cloning");
            var path = Path.Combine(targetReposDirectory, repo.Name);
            if (!Directory.Exists(path))
            {
                fails.Add($"{repo.Name}: {repo.SshUrl} ({path})");
            }
        }
        else
        {
            WriteLine($"Updating {repo.Name}");
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
        Console.Error.WriteLine($"=== Error with {repoName}! ===");
    }
    Console.Error.WriteLine(ex);
    while (true)
    {
        var errorLine = $"{ex.Message}{Environment.NewLine}{ex.StackTrace}";
        fails.Add(errorLine);
        if (ex.InnerException is not null)
        {
            ex = ex.InnerException;
            continue;
        }
        break;
    }
}

static void PullRepo(string workingDirectory, string repoName)
{
    var processStartInfo = new ProcessStartInfo
    {
        WorkingDirectory = workingDirectory,
        FileName = "git",
        Arguments = "pull",
        CreateNoWindow = false
    };

    WriteLine($"Starting {repoName}");
    WriteLine(Process.Start(processStartInfo)?.WaitForExit(1000 * 30));
    WriteLine($"Ending {repoName}");
}

static void WriteLine(object? line) =>
    Console.WriteLine($"{DateTime.Now:O}: {line}");
