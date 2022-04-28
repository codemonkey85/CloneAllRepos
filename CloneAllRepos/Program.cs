using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text;

IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var targetDirectory = config?.GetValue<string>("targetDirectory") ?? string.Empty;
var repos = config?.GetSection("reposToClone").Get<string[]>() ?? Array.Empty<string>();

IList<string> fails = new List<string>();

CloneRepos(targetDirectory, repos);
foreach (var fail in fails)
{
    Console.WriteLine(fail);
}

void CloneRepos(string targetDirectory, string[] repos)
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

void CloneRepo(string targetDirectory, string repo)
{
    try
    {
        if (string.Equals("git@github.com:codemonkey85/CloneAllRepos.git", repo, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var repoName = repo.Replace("git@github.com:codemonkey85/", string.Empty).Replace(".git", string.Empty);
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = targetDirectory,
            FileName = "git",
            Arguments = $"clone {repo} --no-tags",
            CreateNoWindow = true,
        };
        Console.WriteLine($"Cloning {repo}");
        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new Exception("Cannot create process");
        }
        if (process.WaitForExit(1000 * 30))
        {
            Console.WriteLine($"Repo {repo} finished cloning");
        }
        else
        {
            Console.WriteLine($"Repo {repo} did not finish cloning");
        }
        var path = Path.Combine(targetDirectory, repoName);
        if (!Directory.Exists(path))
        {
            fails.Add(path);
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
