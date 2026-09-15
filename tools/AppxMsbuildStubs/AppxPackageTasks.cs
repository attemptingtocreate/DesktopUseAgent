using Microsoft.Build.Framework;
using MsBuildTask = Microsoft.Build.Utilities.Task;

namespace Microsoft.Build.AppxPackage;

public sealed class ExpandPayloadDirectories : MsBuildTask
{
    public ITaskItem[]? Inputs { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public ITaskItem[] Expanded { get; set; } = Array.Empty<ITaskItem>();
    public override bool Execute()
    {
        Expanded = Inputs ?? Array.Empty<ITaskItem>();
        return true;
    }
}

public sealed class RemovePayloadDuplicates : MsBuildTask
{
    public ITaskItem[]? Inputs { get; set; }
    public string? ProjectName { get; set; }
    public string? Platform { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();
    public override bool Execute()
    {
        Filtered = Inputs ?? Array.Empty<ITaskItem>();
        return true;
    }
}

public sealed class GetSdkFileFullPath : MsBuildTask
{
    public string? FileName { get; set; }
    public string? Filename { get => FileName; set => FileName = value; }
    public string? FullFilePath { get; set; }
    public string? FileArchitecture { get; set; }
    public string? RequireExeExtension { get; set; }
    public string? TargetPlatformSdkRootOverride { get; set; }
    public string? SDKIdentifier { get; set; }
    public string? SDKVersion { get; set; }
    public string? TargetPlatformIdentifier { get; set; }
    public string? TargetPlatformMinVersion { get; set; }
    public string? TargetPlatformVersion { get; set; }
    public string? MSBuildExtensionsPath64Exists { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public string ActualFullFilePath { get; set; } = string.Empty;
    [Output] public string ActualFileArchitecture { get; set; } = "x64";
    [Output] public string Path { get; set; } = string.Empty;
    public override bool Execute()
    {
        ActualFullFilePath = FullFilePath ?? string.Empty;
        Path = ActualFullFilePath;
        ActualFileArchitecture = FileArchitecture ?? "x64";
        return true;
    }
}

public sealed class GetDefaultResourceLanguage : MsBuildTask
{
    public string? DefaultLanguage { get; set; }
    public ITaskItem[]? SourceAppxManifest { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public string DefaultResourceLanguage { get; set; } = "en-US";
    [Output] public string Language { get; set; } = "en-US";
    public override bool Execute()
    {
        DefaultResourceLanguage = string.IsNullOrWhiteSpace(DefaultLanguage) ? "en-US" : DefaultLanguage!;
        Language = DefaultResourceLanguage;
        return true;
    }
}

public sealed class GetPackageArchitecture : MsBuildTask
{
    public string? PlatformTarget { get; set; }
    public string? Platform { get; set; }
    public string? ProjectName { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public string PackageArchitecture { get; set; } = "x64";
    [Output] public string Architecture { get; set; } = "x64";
    public override bool Execute()
    {
        var p = PlatformTarget ?? Platform;
        PackageArchitecture = string.IsNullOrWhiteSpace(p) || p == "AnyCPU" ? "x64" : p!;
        Architecture = PackageArchitecture;
        return true;
    }
}

public sealed class GetSdkPropertyValue : MsBuildTask
{
    public string? TargetPlatformSdkRootOverride { get; set; }
    public string? SDKIdentifier { get; set; }
    public string? SDKVersion { get; set; }
    public string? TargetPlatformIdentifier { get; set; }
    public string? TargetPlatformMinVersion { get; set; }
    public string? TargetPlatformVersion { get; set; }
    public string? PropertyName { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public string PropertyValue { get; set; } = string.Empty;
    [Output] public string Value { get; set; } = string.Empty;
    public override bool Execute() => true;
}

public sealed class RemoveRedundantXamlFilesFromSdkPayload : MsBuildTask
{
    public ITaskItem[]? Inputs { get; set; }
    public string? VsTelemetrySession { get; set; }
    [Output] public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();
    public override bool Execute()
    {
        Filtered = Inputs ?? Array.Empty<ITaskItem>();
        return true;
    }
}

public sealed class ValidateConfiguration : MsBuildTask
{
    public string? Platform { get; set; }
    public string? Configuration { get; set; }
    public string? ProjectName { get; set; }
    public string? AppxPackage { get; set; }
    public string? WindowsAppContainer { get; set; }
    public string? OutputType { get; set; }
    public string? VsTelemetrySession { get; set; }
    public override bool Execute() => true;
}
