using Microsoft.Extensions.Configuration;
using Octokit;
using System.Diagnostics;
using System.Text;

IList<string> fails = new List<string>();
var targetReposDirectory = string.Empty;

try
{
    IConfiguration config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", true, true)
        .AddUserSecrets(typeof(Program).Assembly)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    targetReposDirectory = config.GetValue("targetDirectory", string.Empty);
    var githubUserName = config.GetValue("githubUserName", string.Empty);
    var personalAccessToken = config.GetValue("personalAccessToken", string.Empty);

    var myUser = new ProductHeaderValue(githubUserName);
    var credentials = new Credentials(personalAccessToken);
    var client = new GitHubClient(myUser) { Credentials = credentials };

    var repos = (await client.Repository.GetAllForCurrent()).ToList();

    foreach (var repo in repos.Where(r => r.Fork))
    {
        try
        {
            var fork = await client.Repository.Get(githubUserName, repo.Name);
            var upstream = fork.Parent;
            var compareResult = await client.Repository.Commit.Compare(upstream.Owner.Login, upstream.Name,
                upstream.DefaultBranch, $"{fork.Owner.Login}:{fork.DefaultBranch}").ConfigureAwait(false);
            if (compareResult.BehindBy > 0)
            {
                Console.WriteLine($"Updating fork of {repo.Name}");
                var upstreamBranchReference = await client.Git.Reference
                    .Get(upstream.Owner.Login, upstream.Name, $"heads/{upstream.DefaultBranch}").ConfigureAwait(false);
                await client.Git.Reference.Update(fork.Owner.Login, fork.Name, $"heads/{fork.DefaultBranch}",
                    new ReferenceUpdate(upstreamBranchReference.Object.Sha)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error with fork '{repo.Name}':");
            LogExceptions(ex, repo.Name);
        }
    }

    var allDirs = Directory.GetDirectories(targetReposDirectory).Select(dir => new DirectoryInfo(dir).Name);
    var repoDirs = repos.Select(repo => repo.Name);

    var remainingDirs = allDirs.Where(dir => !repoDirs.Contains(dir, StringComparer.OrdinalIgnoreCase));

    foreach (var repo in repos)
    {
        CloneOrUpdateRepo(targetReposDirectory, repo);
    }

    foreach (var repo in remainingDirs)
    {
        var destinationPath = Path.Combine(targetReposDirectory, repo);
        PullRepo(destinationPath);
    }
}
catch (Exception ex)
{
    LogExceptions(ex);
}
finally
{
    if (fails.Count > 0 && targetReposDirectory is { Length: > 0 })
    {
        var sbFails = new StringBuilder();
        sbFails.AppendLine($"{Environment.NewLine}Fails:{Environment.NewLine}");
        foreach (var fail in fails)
        {
            sbFails.AppendLine(fail);
        }

        Console.WriteLine(sbFails);
        File.WriteAllText(Path.Combine(targetReposDirectory, "log.txt"), sbFails.ToString());
    }
}

void CloneOrUpdateRepo(string targetDirectory, Repository repo)
{
    try
    {
        var destinationPath = Path.Combine(targetDirectory, repo.Name);
        if (!Directory.Exists(destinationPath))
        {
            Console.WriteLine($"Cloning {repo.Name}");
            var process = Process.Start(new ProcessStartInfo
            {
                WorkingDirectory = targetDirectory,
                FileName = "git",
                Arguments = $"clone {repo.SshUrl} --no-tags",
                CreateNoWindow = true
            });
            if (process is null)
            {
                throw new Exception("Cannot create process");
            }

            Console.WriteLine(process.WaitForExit(1000 * 30)
                ? $"Repo {repo.Name} finished cloning"
                : $"Repo {repo.Name} did not finish cloning");
            var path = Path.Combine(targetDirectory, repo.Name);
            if (!Directory.Exists(path))
            {
                fails.Add($"{repo.Name}: {repo.SshUrl} ({path})");
            }
        }
        else
        {
            Console.WriteLine($"Updating {repo.Name}");
            PullRepo(destinationPath);
            //PushRepo(destinationPath);
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

static Process? PullRepo(string workingDirectory) => Process.Start(new ProcessStartInfo
{
    WorkingDirectory = workingDirectory,
    FileName = "git",
    Arguments = "pull",
    CreateNoWindow = false
});

//static Process? PushRepo(string workingDirectory) => Process.Start(new ProcessStartInfo
//{
//    WorkingDirectory = workingDirectory,
//    FileName = "git",
//    Arguments = "push",
//    CreateNoWindow = false
//});
