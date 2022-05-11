using Microsoft.Extensions.Configuration;
using Octokit;
using System.Diagnostics;
using System.Text;
using ProductHeaderValue = Octokit.ProductHeaderValue;

IList<string> fails = new List<string>();
try
{
    IConfiguration config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddUserSecrets(typeof(Program).Assembly)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var targetDirectory = config.GetValue("targetDirectory", string.Empty);
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
            var compareResult = await client.Repository.Commit.Compare(upstream.Owner.Login, upstream.Name, upstream.DefaultBranch, $"{fork.Owner.Login}:{fork.DefaultBranch}").ConfigureAwait(false);
            if (compareResult.BehindBy > 0)
            {
                var upstreamBranchReference = await client.Git.Reference.Get(upstream.Owner.Login, upstream.Name, $"heads/{upstream.DefaultBranch}").ConfigureAwait(false);
                await client.Git.Reference.Update(fork.Owner.Login, fork.Name, $"heads/{fork.DefaultBranch}", new ReferenceUpdate(upstreamBranchReference.Object.Sha)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error with fork '{repo.Name}':");
            LogExceptions(ex);
        }
    }

    var allDirs = Directory.GetDirectories(targetDirectory).Select(dir => new DirectoryInfo(dir).Name);
    var repoDirs = repos.Select(repo => repo.Name);

    var remainingDirs = allDirs.Where(dir => !repoDirs.Contains(dir, StringComparer.OrdinalIgnoreCase));

    foreach (var repo in repos)
    {
        CloneRepo(targetDirectory, repo);
    }

    foreach (var repo in remainingDirs)
    {
        var destinationPath = Path.Combine(targetDirectory, repo);
        var process = Process.Start(new ProcessStartInfo
        {
            WorkingDirectory = destinationPath,
            FileName = "git",
            Arguments = $"pull",
            CreateNoWindow = false,
        });
    }

    if (fails.Count > 0)
    {
        var sbFails = new StringBuilder();
        sbFails.AppendLine($"{Environment.NewLine}Fails:{Environment.NewLine}");
        foreach (var fail in fails)
        {
            sbFails.AppendLine(fail);
        }
        Console.WriteLine(sbFails);
        File.WriteAllText(Path.Combine(targetDirectory, "log.txt"), sbFails.ToString());
    }
}
catch (Exception ex)
{
    LogExceptions(ex);
}

void CloneRepo(string targetDirectory, Repository repo)
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
                CreateNoWindow = true,
            });
            if (process is null)
            {
                throw new Exception("Cannot create process");
            }
            if (process.WaitForExit(1000 * 30))
            {
                Console.WriteLine($"Repo {repo.Name} finished cloning");
            }
            else
            {
                Console.WriteLine($"Repo {repo.Name} did not finish cloning");
            }
            var path = Path.Combine(targetDirectory, repo.Name);
            if (!Directory.Exists(path))
            {
                fails.Add($"{repo.Name}: {repo.SshUrl} ({path})");
            }
        }
        else
        {
            var process = Process.Start(new ProcessStartInfo
            {
                WorkingDirectory = destinationPath,
                FileName = "git",
                Arguments = $"pull",
                CreateNoWindow = false,
            });
        }
    }
    catch (Exception ex)
    {
        LogExceptions(ex);
    }
}

void LogExceptions(Exception ex)
{
    Console.Error.WriteLine($"{ex.Message}{Environment.NewLine}{ex.StackTrace}");
    if (ex.InnerException is not null)
    {
        LogExceptions(ex.InnerException);
    }
}
