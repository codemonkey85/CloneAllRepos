using Microsoft.Extensions.Configuration;
using Octokit;
using System.Diagnostics;
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

    var repos = await client.Repository.GetAllForCurrent();

    foreach (var repo in repos)
    {
        CloneRepo(targetDirectory, repo);
    }

    if (fails.Count > 0)
    {
        Console.WriteLine($"{Environment.NewLine}Fails:{Environment.NewLine}");
        foreach (var fail in fails)
        {
            Console.WriteLine(fail);
        }
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
    Console.WriteLine($"{ex.Message}{Environment.NewLine}{ex.StackTrace}");
    if (ex.InnerException is not null)
    {
        LogExceptions(ex.InnerException);
    }
}
