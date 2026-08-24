using System;
using System.IO;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Frosting;
using Cake.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace CakeBuild;

public static class Program
{
    public static int Main(string[] args)
    {
        return new CakeHost()
            .UseContext<BuildContext>()
            .Run(args);
    }
}

public class BuildContext : FrostingContext
{
    public const string ProjectName = "seamarks";

    // The mod project sits in its own folder, a sibling of this one, with the csproj, modinfo.json and
    // assets all alongside each other inside it.
    public const string ModRoot = "../Seamarks";
    public const string ReleasesDir = "../Releases";

    public string BuildConfiguration { get; }
    public string Version { get; }
    public string Name { get; }
    public bool SkipJsonValidation { get; }

    public BuildContext(ICakeContext context)
        : base(context)
    {
        BuildConfiguration = context.Argument("configuration", "Release");
        SkipJsonValidation = context.Argument("skipJsonValidation", false);
        var modInfo = context.DeserializeJsonFromFile<ModInfo>($"{ModRoot}/modinfo.json");
        Version = modInfo.Version;
        Name = modInfo.ModID;
    }
}

[TaskName("ValidateJson")]
public sealed class ValidateJsonTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        if (context.SkipJsonValidation)
        {
            return;
        }
        var jsonFiles = context.GetFiles($"{BuildContext.ModRoot}/assets/**/*.json");
        foreach (var file in jsonFiles)
        {
            try
            {
                var json = File.ReadAllText(file.FullPath);
                JToken.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new Exception($"Validation failed for JSON file: {file.FullPath}{Environment.NewLine}{ex.Message}", ex);
            }
        }
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(ValidateJsonTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetClean($"{BuildContext.ModRoot}/{BuildContext.ProjectName}.csproj",
            new DotNetCleanSettings
            {
                Configuration = context.BuildConfiguration
            });

        context.DotNetPublish($"{BuildContext.ModRoot}/{BuildContext.ProjectName}.csproj",
            new DotNetPublishSettings
            {
                Configuration = context.BuildConfiguration
            });
    }
}

[TaskName("Package")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var staging = $"{BuildContext.ReleasesDir}/{context.Name}";
        context.EnsureDirectoryExists(BuildContext.ReleasesDir);
        context.CleanDirectory(BuildContext.ReleasesDir);
        context.EnsureDirectoryExists(staging);

        // The assembly only. Publish leaves a .pdb and a .deps.json beside it that a player has no use
        // for, and the Debug zip the csproj builds has always left both out too.
        context.CopyFiles(
            $"{BuildContext.ModRoot}/bin/{context.BuildConfiguration}/Mods/mod/publish/*.dll", staging);

        // Taken from the source tree, not from the publish folder. The csproj copies them to the output as
        // well, and reading the originals keeps this working if that ever stops being true.
        if (context.DirectoryExists($"{BuildContext.ModRoot}/assets"))
        {
            context.CopyDirectory($"{BuildContext.ModRoot}/assets", $"{staging}/assets");
        }
        context.CopyFile($"{BuildContext.ModRoot}/modinfo.json", $"{staging}/modinfo.json");
        if (context.FileExists($"{BuildContext.ModRoot}/modicon.png"))
        {
            context.CopyFile($"{BuildContext.ModRoot}/modicon.png", $"{staging}/modicon.png");
        }

        context.Zip(staging, $"{BuildContext.ReleasesDir}/{context.Name}_{context.Version}.zip");
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PackageTask))]
public class DefaultTask : FrostingTask
{
}
