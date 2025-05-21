namespace CloneAllRepos;

public class AppConfig
{
    public string? PersonalAccessToken { get; init; }

    public string? GithubUserName { get; init; }

    public string? TargetDirectory { get; init; }

    public List<string> OwnersToInclude { get; init; } = [];
}
