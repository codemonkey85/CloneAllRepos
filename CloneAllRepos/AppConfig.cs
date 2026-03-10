namespace CloneAllRepos;

public class AppConfig
{
    public string? GithubUserName { get; init; }

    public string? TargetDirectory { get; init; }

    public List<string> OwnersToInclude { get; init; } = [];

    public List<string> ForceSyncRepos { get; init; } = [];

    public int RepoLimit { get; init; } = 1000;
}
