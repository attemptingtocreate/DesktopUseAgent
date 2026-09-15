using Microsoft.Build.Framework;
using MsBuildTask = Microsoft.Build.Utilities.Task;

namespace Microsoft.Build.Packaging.Pri.Tasks;

public sealed class ExpandPriContent : MsBuildTask
{
    public ITaskItem[]? Inputs { get; set; }
    public string? MakePriExeFullPath { get; set; }
    public string? MakePriExtensionPath { get; set; }
    public string? IntermediateDirectory { get; set; }
    public string? AdditionalMakepriExeParameters { get; set; }
    public string? ExcludeXamlFromLibraryLayoutsWhenXbfIsPresent { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public ITaskItem[] Expanded { get; set; } = Array.Empty<ITaskItem>();
    [Output] public ITaskItem[] IntermediateFileWrites { get; set; } = Array.Empty<ITaskItem>();
    public override bool Execute()
    {
        Expanded = Array.Empty<ITaskItem>();
        IntermediateFileWrites = Array.Empty<ITaskItem>();
        return true;
    }
}

public class FlexibleNoopTask : MsBuildTask
{
    public string? PriConfigXmlPath { get; set; }
    public string? LayoutResfilesPath { get; set; }
    public string? ResourcesResfilesPath { get; set; }
    public string? PriResfilesPath { get; set; }
    public string? EmbedFileResfilePath { get; set; }
    public string? PriInitialPath { get; set; }
    public string? DefaultResourceLanguage { get; set; }
    public string? DefaultResourceQualifiers { get; set; }
    public string? IntermediateExtension { get; set; }
    public string? PriConfigXmlDefaultSnippetPath { get; set; }
    public string? TargetPlatformIdentifier { get; set; }
    public string? TargetPlatformVersion { get; set; }
    public ITaskItem[]? AdditionalResourceResFiles { get; set; }
    public string? VsTelemetrySession { get; set; }
    public string? MakePriExeFullPath { get; set; }
    public string? MakePriExtensionPath { get; set; }
    public string? IndexFilesForQualifiersCollection { get; set; }
    public string? ProjectPriIndexName { get; set; }
    public string? InsertReverseMap { get; set; }
    public string? ProjectDirectory { get; set; }
    public string? OutputFileName { get; set; }
    public string? QualifiersPath { get; set; }
    public string? AppxBundleAutoResourcePackageQualifiers { get; set; }
    public string? MultipleQualifiersPerDimensionFoundPath { get; set; }
    public string? AdditionalMakepriExeParameters { get; set; }
    public ITaskItem[]? Inputs { get; set; }
    public string? ProjectName { get; set; }
    public string? Platform { get; set; }
    public override bool Execute() => true;
}

public sealed class CreatePriConfigXmlForSplitting : FlexibleNoopTask { }
public sealed class CreatePriConfigXmlForMainPackageFileMap : FlexibleNoopTask { }
public sealed class CreatePriConfigXmlForFullIndex : FlexibleNoopTask { }
public sealed class CreatePriFilesForPortableLibraries : FlexibleNoopTask { }
public sealed class GenerateMainPriConfigurationFile : FlexibleNoopTask { }
public sealed class GeneratePriConfigurationFiles : FlexibleNoopTask { }
public sealed class GenerateProjectPriFile : FlexibleNoopTask { }
public sealed class RemoveDuplicatePriFiles : FlexibleNoopTask { }
public sealed class UpdateMainPackageFileMap : FlexibleNoopTask { }
