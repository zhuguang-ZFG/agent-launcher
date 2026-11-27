using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class TestIsolation
{
    public static void Require()
    {
#if !INTEGRATION_TESTS
        throw new InvalidOperationException("Stateful tests require an INTEGRATION_TESTS build. No state was accessed.");
#else
        var requested=Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT");
        if(string.IsNullOrWhiteSpace(requested)||!Path.IsPathFullyQualified(requested))throw new InvalidOperationException("Stateful tests require an explicit absolute test root.");
        if(requested.StartsWith(@"\\?\",StringComparison.Ordinal)||requested.StartsWith(@"\\.\",StringComparison.Ordinal))throw new InvalidOperationException("Test roots must use a regular absolute path, not a device path.");
        var expected=Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        if(!Path.GetFileName(expected).StartsWith("AgentLauncher-tests-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Test root directory must start with AgentLauncher-tests-.");
        if(!string.Equals(expected,Path.TrimEndingDirectorySeparator(Store.Root),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Referenced Windows assembly ignored the test root; stateful tests refused.");
        var production=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AgentLauncher");
        if(expected.StartsWith(production+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||string.Equals(expected,production,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Tests may not run inside production state.");
        for(var directory=new DirectoryInfo(expected);directory is not null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Test roots cannot traverse directory links.");
        var physicalExpected=CanonicalPath(expected);var physicalProduction=CanonicalPath(production);
        if(physicalExpected.StartsWith(physicalProduction+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||string.Equals(physicalExpected,physicalProduction,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Tests may not reach production state through a path alias.");
#endif
    }

    private static string CanonicalPath(string path)
    {
        var suffix=new Stack<string>();var existing=new DirectoryInfo(path);
        while(!existing.Exists)
        {
            suffix.Push(existing.Name);
            existing=existing.Parent??throw new InvalidOperationException("Test root has no existing local ancestor.");
        }
        var resolved=PhysicalDirectory.Resolve(existing.FullName).Path;
        foreach(var segment in suffix)resolved=Path.Combine(resolved,segment);
        return Path.TrimEndingDirectorySeparator(resolved);
    }
}
