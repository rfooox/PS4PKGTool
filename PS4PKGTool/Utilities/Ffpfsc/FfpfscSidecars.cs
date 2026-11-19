#nullable enable
using System;
using System.Collections.Generic;
using PS4PKGTool.Utilities.Ffpfsc.Engine;

namespace PS4PKGTool.Utilities.Ffpfsc;

/// <summary>
/// Options for a single-PKG FFPFSC conversion. Mirrors the shape of
/// <c>OrbisPkgTool.PkgMergeRequest</c> so the UI can reuse the same form
/// layout pattern as <c>PkgMergeOptionsForm</c>.
/// </summary>
public sealed record FfpfscConvertOptions(
    string PkgPath,
    string OutputPath,
    string WorkParentDirectory,
    string? Passcode = null,
    bool VerifyAfterBuild = true,
    bool KeepWorkDirectory = false);

/// <summary>
/// Progress report for the converter pipeline. <c>Stage</c> is a short label
/// shown in the UI ("正在提取 PKG", "正在构建 FFPFSC 镜像", "Verifying");
/// sub-progress is carried either as item counts (<c>ItemsProcessed/ItemsTotal</c>,
/// e.g. extracted files) or byte counts (<c>BytesProcessed/TotalBytes</c>,
/// e.g. PFSC compression); whichever is non-zero drives the current bar.
/// </summary>
public sealed record FfpfscConvertProgress(
    string Stage,
    int CurrentStep,
    int TotalSteps,
    long BytesProcessed = 0,
    long TotalBytes = 0,
    string? CurrentFile = null,
    int ItemsProcessed = 0,
    int ItemsTotal = 0);

/// <summary>
/// Result of a successful conversion. Mirrors the key fields the UI shows in
/// the merge results dialog.
/// </summary>
public sealed record FfpfscConvertResult(
    string OutputPath,
    string InnerFileName,
    long SourceLength,
    long ContainerLength,
    int PfscBlockCount,
    int CompressedBlockCount,
    double SavingsPercent,
    FfpfscVerificationResult? Verification,
    bool NpbindFooterRepaired);

/// <summary>Step labels for the <c>FfpfscConvertProgress.Stage</c> field.</summary>
public static class FfpfscConvertStages
{
    public const string Extracting = "正在提取 PKG";
    public const string Restructuring = "正在重构系统文件";
    public const string ProjectingParamJson = "正在生成 param.json";
    public const string RepairingNpbind = "正在修复 npbind.dat 尾部";
    public const string Building = "正在构建 FFPFSC 镜像";
    public const string Verifying = "正在校验 FFPFSC";
    public const string CleaningUp = "正在清理";
    public const string Done = "Done";
}
