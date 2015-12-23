﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using LibGit2Sharp;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Nerdbank.GitVersioning;
using Nerdbank.GitVersioning.Tests;
using Validation;
using Xunit;
using Xunit.Abstractions;
using Version = System.Version;

public class BuildIntegrationTests : RepoTestBase
{
    private const string GitVersioningTargetsFileName = "NerdBank.GitVersioning.targets";
    private BuildManager buildManager;
    private ProjectCollection projectCollection;
    private string projectDirectory;
    private ProjectRootElement testProject;
    private Dictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private Random random;

    public BuildIntegrationTests(ITestOutputHelper logger)
        : base(logger)
    {
        int seed = (int)DateTime.Now.Ticks;
        this.random = new Random(seed);
        this.Logger.WriteLine("Random seed: {0}", seed);
        this.buildManager = new BuildManager();
        this.projectCollection = new ProjectCollection();
        this.projectDirectory = Path.Combine(this.RepoPath, "projdir");
        Directory.CreateDirectory(this.projectDirectory);
        this.LoadTargetsIntoProjectCollection();
        this.testProject = this.CreateProjectRootElement();
        this.globalProperties.Add("NerdbankGitVersioningTasksPath", Environment.CurrentDirectory + "\\");
    }

    [Fact]
    public async Task GetBuildVersion_Returns_BuildVersion_Property()
    {
        this.WriteVersionFile();
        this.InitializeSourceControl();
        var buildResult = await this.BuildAsync();
        Assert.Equal(
            buildResult.BuildVersion,
            buildResult.BuildResult.ResultsByTarget[Targets.GetBuildVersion].Items.Single().ItemSpec);
    }

    [Fact]
    public async Task GetBuildVersion_Without_Git()
    {
        this.WriteVersionFile("3.4");
        var buildResult = await this.BuildAsync();
        Assert.Equal("3.4", buildResult.BuildVersion);
        Assert.Equal("3.4.0", buildResult.AssemblyInformationalVersion);
    }

    [Fact]
    public async Task GetBuildVersion_In_Git_But_Without_Commits()
    {
        Repository.Init(this.RepoPath);
        var repo = new Repository(this.RepoPath); // do not assign Repo property to avoid commits being generated later
        this.WriteVersionFile("3.4");
        Assumes.False(repo.Head.Commits.Any()); // verification that the test is doing what it claims
        var buildResult = await this.BuildAsync();
        Assert.Equal("3.4.0.0", buildResult.BuildVersion);
        Assert.Equal("3.4.0", buildResult.AssemblyInformationalVersion);
    }

    [Fact]
    public async Task GetBuildVersion_In_Git_But_Head_Lacks_VersionFile()
    {
        Repository.Init(this.RepoPath);
        var repo = new Repository(this.RepoPath); // do not assign Repo property to avoid commits being generated later
        repo.Commit("empty", this.Signer, this.Signer, new CommitOptions { AllowEmptyCommit = true });
        this.WriteVersionFile("3.4");
        Assumes.True(repo.Index[VersionFile.JsonFileName] == null);
        var buildResult = await this.BuildAsync();
        Assert.Equal("3.4.0." + repo.Head.Commits.First().GetIdAsVersion().Revision, buildResult.BuildVersion);
        Assert.Equal("3.4.0+g" + repo.Head.Commits.First().Id.Sha.Substring(0, 10), buildResult.AssemblyInformationalVersion);
    }

    [Fact]
    public async Task GetBuildVersion_In_Git_But_WorkingCopy_Has_Changes()
    {
        const string majorMinorVersion = "5.8";
        const string prerelease = "";

        this.WriteVersionFile(majorMinorVersion, prerelease);
        this.InitializeSourceControl();
        var workingCopyVersion = VersionOptions.FromVersion(new Version("6.0"));
        VersionFile.SetVersion(this.RepoPath, workingCopyVersion);
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(workingCopyVersion, buildResult);
    }

    [Fact]
    public async Task GetBuildVersion_In_Git_No_VersionFile_At_All()
    {
        Repository.Init(this.RepoPath);
        var repo = new Repository(this.RepoPath); // do not assign Repo property to avoid commits being generated later
        repo.Commit("empty", this.Signer, this.Signer, new CommitOptions { AllowEmptyCommit = true });
        var buildResult = await this.BuildAsync();
        Assert.Equal("0.0.1." + repo.Head.Commits.First().GetIdAsVersion().Revision, buildResult.BuildVersion);
        Assert.Equal("0.0.1+g" + repo.Head.Commits.First().Id.Sha.Substring(0, 10), buildResult.AssemblyInformationalVersion);
    }

    [Fact]
    public async Task GetBuildVersion_In_Git_With_Version_File_In_Subdirectory_Works()
    {
        const string majorMinorVersion = "5.8";
        const string prerelease = "";
        const string subdirectory = "projdir";

        this.WriteVersionFile(majorMinorVersion, prerelease, subdirectory);
        this.InitializeSourceControl();
        this.AddCommits(this.random.Next(15));
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(VersionOptions.FromVersion(new Version(majorMinorVersion)), buildResult, subdirectory);
    }

    [Fact]
    public async Task GetBuildVersion_In_Git_With_Version_File_In_Root_And_Subdirectory_Works()
    {
        var rootVersionSpec = new VersionOptions { Version = SemanticVersion.Parse("14.1"), AssemblyVersion = new Version(14, 0) };
        var subdirVersionSpec = new VersionOptions { Version = SemanticVersion.Parse("11.0") };
        const string subdirectory = "projdir";

        this.WriteVersionFile(rootVersionSpec);
        this.WriteVersionFile(subdirVersionSpec, subdirectory);
        this.InitializeSourceControl();
        this.AddCommits(this.random.Next(15));
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(subdirVersionSpec, buildResult, subdirectory);
    }

    [Fact]
    public async Task GetBuildVersion_StablePreRelease()
    {
        const string majorMinorVersion = "5.8";
        const string prerelease = "";

        this.WriteVersionFile(majorMinorVersion, prerelease);
        this.InitializeSourceControl();
        this.AddCommits(this.random.Next(15));
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(VersionOptions.FromVersion(new Version(majorMinorVersion)), buildResult);
    }

    [Fact]
    public async Task GetBuildVersion_StableRelease()
    {
        const string majorMinorVersion = "5.8";
        const string prerelease = "";

        this.WriteVersionFile(majorMinorVersion, prerelease);
        this.InitializeSourceControl();
        this.AddCommits(this.random.Next(15));
        this.globalProperties.Add("PublicRelease", "true");
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(VersionOptions.FromVersion(new Version(majorMinorVersion)), buildResult);

        Version version = this.Repo.Head.Commits.First().GetIdAsVersion();
        Assert.Equal($"{version.Major}.{version.Minor}.{buildResult.GitVersionHeight}", buildResult.NuGetPackageVersion);
    }

    [Fact]
    public async Task GetBuildVersion_UnstablePreRelease()
    {
        const string majorMinorVersion = "5.8";
        const string prerelease = "-beta";

        this.WriteVersionFile(majorMinorVersion, prerelease);
        this.InitializeSourceControl();
        this.AddCommits(this.random.Next(15));
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(VersionOptions.FromVersion(new Version(majorMinorVersion), prerelease), buildResult);
    }

    [Fact]
    public async Task GetBuildVersion_UnstableRelease()
    {
        const string majorMinorVersion = "5.8";
        const string prerelease = "-beta";

        this.WriteVersionFile(majorMinorVersion, prerelease);
        this.InitializeSourceControl();
        this.AddCommits(this.random.Next(15));
        this.globalProperties.Add("PublicRelease", "true");
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(VersionOptions.FromVersion(new Version(majorMinorVersion), prerelease), buildResult);
    }

    [Fact]
    public async Task GetBuildVersion_CustomAssemblyVersion()
    {
        this.WriteVersionFile("14.0");
        this.InitializeSourceControl();
        var versionOptions = new VersionOptions
        {
            Version = new SemanticVersion(new Version(14, 1)),
            AssemblyVersion = new Version(14, 0),
        };
        this.WriteVersionFile(versionOptions);
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(versionOptions, buildResult);
    }

    [Fact]
    public async Task GetBuildVersion_CustomBuildNumberOffset()
    {
        this.WriteVersionFile("14.0");
        this.InitializeSourceControl();
        var versionOptions = new VersionOptions
        {
            Version = new SemanticVersion(new Version(14, 1)),
            BuildNumberOffset = 5,
        };
        this.WriteVersionFile(versionOptions);
        var buildResult = await this.BuildAsync();
        this.AssertStandardProperties(versionOptions, buildResult);
    }

    [Fact]
    public async Task PublicRelease_RegEx_Unsatisfied()
    {
        var versionOptions = new VersionOptions
        {
            Version = SemanticVersion.Parse("1.0"),
            PublicReleaseRefSpec = "^refs/heads/release$",
        };
        this.WriteVersionFile(versionOptions);
        this.InitializeSourceControl();

        // Just build "master", which doesn't conform to the regex.
        var buildResult = await this.BuildAsync();
        Assert.False(buildResult.PublicRelease);
        AssertStandardProperties(versionOptions, buildResult);
    }

    public static IEnumerable<object[]> CIServerBuilds
    {
        get
        {
            return new object[][]
            {
                new object[] {
                    new Dictionary<string, string> {
                        { "APPVEYOR", "True" },
                        { "APPVEYOR_REPO_BRANCH", "release" },
                    },
                },
                new object[]
                {
                    new Dictionary<string, string>
                    {
                        { "SYSTEM_TEAMPROJECTID", "1" },
                        { "BUILD_SOURCEBRANCH", "refs/heads/release" },
                    },
                },
            };
        }
    }

    [Theory]
    [MemberData(nameof(CIServerBuilds))]
    public async Task PublicRelease_RegEx_SatisfiedByCI(IReadOnlyDictionary<string, string> serverProperties)
    {
        var versionOptions = new VersionOptions
        {
            Version = SemanticVersion.Parse("1.0"),
            PublicReleaseRefSpec = "^refs/heads/release$",
        };
        this.WriteVersionFile(versionOptions);
        this.InitializeSourceControl();

        // Don't actually switch the checked out branch in git. CI environment variables
        // should take precedence over actual git configuration. (Why? because these variables may
        // retain information about which tag was checked out on a detached head).
        foreach (var property in serverProperties)
        {
            this.globalProperties.Add(property.Key, property.Value);
        }

        var buildResult = await this.BuildAsync();
        Assert.True(buildResult.PublicRelease);
        AssertStandardProperties(versionOptions, buildResult);
    }

    [Fact]
    public async Task PublicRelease_RegEx_SatisfiedByCheckedOutBranch()
    {
        var versionOptions = new VersionOptions
        {
            Version = SemanticVersion.Parse("1.0"),
            PublicReleaseRefSpec = "^refs/heads/release$",
        };
        this.WriteVersionFile(versionOptions);
        this.InitializeSourceControl();

        // Check out a branch that conforms.
        var releaseBranch = this.Repo.CreateBranch("release");
        this.Repo.Checkout(releaseBranch);
        var buildResult = await this.BuildAsync();
        Assert.True(buildResult.PublicRelease);
        AssertStandardProperties(versionOptions, buildResult);
    }

    private void AssertStandardProperties(VersionOptions versionOptions, BuildResults buildResult, string relativeProjectDirectory = null)
    {
        int versionHeight = this.Repo.GetVersionHeight(relativeProjectDirectory);
        Version idAsVersion = this.Repo.GetIdAsVersion(relativeProjectDirectory);
        string commitIdShort = this.Repo.Head.Commits.First().Id.Sha.Substring(0, 10);
        Version version = this.Repo.GetIdAsVersion(relativeProjectDirectory);
        Version assemblyVersion = (versionOptions.AssemblyVersion ?? versionOptions.Version.Version).EnsureNonNegativeComponents();
        Assert.Equal($"{version}", buildResult.AssemblyFileVersion);
        Assert.Equal($"{idAsVersion.Major}.{idAsVersion.Minor}.{idAsVersion.Build}{versionOptions.Version.Prerelease}+g{commitIdShort}", buildResult.AssemblyInformationalVersion);

        // The assembly version property should always have four integer components to it,
        // per bug https://github.com/AArnott/Nerdbank.GitVersioning/issues/26
        Assert.Equal($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}.{assemblyVersion.Revision}", buildResult.AssemblyVersion);

        Assert.Equal(idAsVersion.Build.ToString(), buildResult.BuildNumber);
        Assert.Equal(idAsVersion.Build.ToString(), buildResult.BuildNumberFirstAndSecondComponentsIfApplicable);
        Assert.Equal(idAsVersion.Build.ToString(), buildResult.BuildNumberFirstComponent);
        Assert.Equal(string.Empty, buildResult.BuildNumberSecondComponent);
        Assert.Equal($"{version}", buildResult.BuildVersion);
        Assert.Equal($"{idAsVersion.Major}.{idAsVersion.Minor}.{idAsVersion.Build}", buildResult.BuildVersion3Components);
        Assert.Equal(idAsVersion.Build.ToString(), buildResult.BuildVersionNumberComponent);
        Assert.Equal($"{idAsVersion.Major}.{idAsVersion.Minor}.{idAsVersion.Build}", buildResult.BuildVersionSimple);
        Assert.Equal(this.Repo.Head.Commits.First().Id.Sha, buildResult.GitCommitId);
        Assert.Equal(commitIdShort, buildResult.GitCommitIdShort);
        Assert.Equal(versionHeight.ToString(), buildResult.GitVersionHeight);
        Assert.Equal($"{version.Major}.{version.Minor}", buildResult.MajorMinorVersion);
        Assert.Equal(versionOptions.Version.Prerelease, buildResult.PrereleaseVersion);
        Assert.Equal($"+g{commitIdShort}", buildResult.SemVerBuildSuffix);

        string pkgVersionSuffix = buildResult.PublicRelease
            ? string.Empty
            : $"-g{commitIdShort}";
        Assert.Equal($"{idAsVersion.Major}.{idAsVersion.Minor}.{idAsVersion.Build}{versionOptions.Version.Prerelease}{pkgVersionSuffix}", buildResult.NuGetPackageVersion);
    }

    private async Task<BuildResults> BuildAsync(string target = Targets.GetBuildVersion)
    {
        var buildResult = await this.buildManager.BuildAsync(
            this.Logger,
            this.projectCollection,
            this.testProject,
            target,
            this.globalProperties);
        var result = new BuildResults(buildResult);
        this.Logger.WriteLine(result.ToString());
        Assert.Equal(BuildResultCode.Success, buildResult.OverallResult);
        return result;
    }

    private void LoadTargetsIntoProjectCollection()
    {
        const string prefix = "NerdBank.GitVersioning.Tests.Targets.";

        var streamNames = from name in Assembly.GetExecutingAssembly().GetManifestResourceNames()
                          where name.StartsWith(prefix, StringComparison.Ordinal)
                          select name;
        foreach (string name in streamNames)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                var targetsFile = ProjectRootElement.Create(XmlReader.Create(stream), this.projectCollection);
                targetsFile.FullPath = Path.Combine(this.RepoPath, name.Substring(prefix.Length));
            }
        }
    }

    private ProjectRootElement CreateProjectRootElement()
    {
        var pre = ProjectRootElement.Create(this.projectCollection);
        pre.FullPath = Path.Combine(this.projectDirectory, "test.proj");
        pre.AddImport(Path.Combine(this.RepoPath, GitVersioningTargetsFileName));
        return pre;
    }

    private static class Targets
    {
        internal const string GetBuildVersion = "GetBuildVersion";
        internal const string GetNuGetPackageVersion = "GetNuGetPackageVersion";
        internal const string GenerateAssemblyInfo = "GenerateAssemblyInfo";
    }

    private class BuildResults
    {
        internal BuildResults(BuildResult buildResult)
        {
            Requires.NotNull(buildResult, nameof(buildResult));
            this.BuildResult = buildResult;
        }

        public BuildResult BuildResult { get; private set; }

        public bool PublicRelease => string.Equals("true", this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("PublicRelease"), StringComparison.OrdinalIgnoreCase);
        public string BuildNumber => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildNumber");
        public string GitCommitId => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("GitCommitId");
        public string BuildVersion => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildVersion");
        public string BuildVersionSimple => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildVersionSimple");
        public string PrereleaseVersion => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("PrereleaseVersion");
        public string MajorMinorVersion => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("MajorMinorVersion");
        public string BuildVersionNumberComponent => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildVersionNumberComponent");
        public string BuildNumberFirstComponent => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildNumberFirstComponent");
        public string BuildNumberSecondComponent => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildNumberSecondComponent");
        public string BuildNumberFirstAndSecondComponentsIfApplicable => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildNumberFirstAndSecondComponentsIfApplicable");
        public string GitCommitIdShort => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("GitCommitIdShort");
        public string GitVersionHeight => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("GitVersionHeight");
        public string SemVerBuildSuffix => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("SemVerBuildSuffix");
        public string BuildVersion3Components => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("BuildVersion3Components");
        public string AssemblyInformationalVersion => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("AssemblyInformationalVersion");
        public string AssemblyFileVersion => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("AssemblyFileVersion");
        public string AssemblyVersion => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("AssemblyVersion");
        public string NuGetPackageVersion => this.BuildResult.ProjectStateAfterBuild.GetPropertyValue("NuGetPackageVersion");

        public override string ToString()
        {
            var sb = new StringBuilder();

            foreach (var property in this.GetType().GetRuntimeProperties().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.DeclaringType == this.GetType() && property.Name != nameof(BuildResult))
                {
                    sb.AppendLine($"{property.Name} = {property.GetValue(this)}");
                }
            }

            return sb.ToString();
        }
    }
}
