using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

public sealed class RepoTreeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repotree-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AWorktreeGitFileStopsTheWalkBeforeTheEnclosingCheckout()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        string worktree = Path.Combine(_root, "wt");
        string deep = Path.Combine(worktree, "a", "b");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere\n");

        Assert.Equal(worktree, RepoTree.FindRoot(deep));
    }

    [Fact]
    public void AGitDirectoryIsARoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        string deep = Path.Combine(_root, "x");
        Directory.CreateDirectory(deep);

        Assert.Equal(_root, RepoTree.FindRoot(deep));
    }
}
