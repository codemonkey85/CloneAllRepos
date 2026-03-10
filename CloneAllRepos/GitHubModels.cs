namespace CloneAllRepos;

public record GitHubRepo(string Name, GitHubOwner Owner, bool IsFork, bool IsArchived, string SshUrl);

public record GitHubOwner(string Login);
