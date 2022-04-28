using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text;

IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var targetDirectory = config.GetValue<string>("targetDirectory");
var repos = config.GetValue<string[]>("reposToClone", Array.Empty<string>());

CloneRepos(targetDirectory, repos);

//using IHost host = Host.CreateDefaultBuilder(args)
//    .ConfigureServices((_, services) => { }
//        //services
//            //.AddTransient<ITransientOperation, DefaultOperation>()
//            //.AddScoped<IScopedOperation, DefaultOperation>()
//            //.AddSingleton<ISingletonOperation, DefaultOperation>()
//            //.AddTransient<OperationLogger>())
//            )
//    .Build();

//await host.RunAsync();

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
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = targetDirectory,
            FileName = "git",
            Arguments = $"clone {repo}",
            CreateNoWindow = true,
        };
        Process.Start(startInfo);
    }
    catch (Exception ex)
    {
        LogExceptions(ex);
    }
}

void LogExceptions(Exception ex)
{
    StringBuilder sbError = new StringBuilder();
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
