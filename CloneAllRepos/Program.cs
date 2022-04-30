using Microsoft.Extensions.Configuration;
using Octokit;
using System.Diagnostics;
using System.Text;
using ProductHeaderValue = Octokit.ProductHeaderValue;

IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddUserSecrets(typeof(Program).Assembly)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var targetDirectory = config?.GetValue<string>("targetDirectory") ?? string.Empty;
var githubUserName = config?.GetValue<string>("githubUserName") ?? string.Empty;
var personalAccessToken = config?.GetValue<string>("personalAccessToken") ?? string.Empty;

IList<string> fails = new List<string>();

var myUser = new ProductHeaderValue(githubUserName);
var credentials = new Credentials(personalAccessToken);
var client = new GitHubClient(myUser) { Credentials = credentials };

var repos = await client.Repository.GetAllForCurrent();

CloneRepos(targetDirectory, repos);
if (fails.Count > 0)
{
    Console.WriteLine($"{Environment.NewLine}Fails:{Environment.NewLine}");
    foreach (var fail in fails)
    {
        Console.WriteLine(fail);
    }
}

void CloneRepos(string targetDirectory, IEnumerable<Repository> repos)
{
    try
    {
        foreach (var repo in repos)
        {
            CloneRepo(targetDirectory, repo);
        }
    }
    catch (Exception ex)
    {
        LogExceptions(ex);
    }
}

void CloneRepo(string targetDirectory, Repository repo)
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = targetDirectory,
            FileName = "git",
            Arguments = $"clone {repo.SshUrl} --no-tags",
            CreateNoWindow = true,
        };
        Console.WriteLine($"Cloning {repo.Name}");
        var process = Process.Start(startInfo);
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
    catch (Exception ex)
    {
        LogExceptions(ex);
    }
}

void LogExceptions(Exception ex)
{
    var sbError = new StringBuilder();
    AddExceptionToLog(sbError, ex);
    Console.WriteLine(sbError);
}

void AddExceptionToLog(StringBuilder sbError, Exception ex)
{
    Console.WriteLine($"{ex.Message}{Environment.NewLine}{ex.StackTrace}");
    if (ex.InnerException is not null)
    {
        AddExceptionToLog(sbError, ex.InnerException);
    }
}
