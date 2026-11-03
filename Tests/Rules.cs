using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ModGates.Tests;

/// <summary>
/// The repo's rules.json, the one place that says where its code is, what its ledger prefix is,
/// and which files it exempts from which rule. Every shared test reads it from here.
/// </summary>
internal sealed class Rules
{
    public string Prod { get; init; } = "";
    public string Tests { get; init; } = "";
    public string TicketPrefix { get; init; } = "";
    public string[] Projects { get; init; } = Array.Empty<string>();
    public string[] TestProjects { get; init; } = Array.Empty<string>();
    public string ModConfig { get; init; } = "mod/ModConfig.json";
    public string? DevDefine { get; init; }
    public string[] TempPrefixes { get; init; } = Array.Empty<string>();
    public string[] TempKeep { get; init; } = Array.Empty<string>();
    public string LoggingDoc { get; init; } = "docs/LOGGING.md";
    public string[] ConsoleAllowlist { get; init; } = Array.Empty<string>();
    public string[] FenceExemptFiles { get; init; } = Array.Empty<string>();
    public string Baselines { get; init; } = "docs/health";
    public Dictionary<string, string> NoSummaryExempt { get; init; } = new();
    public Dictionary<string, string> ImageAllowlist { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PinTables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Pins { get; init; } = new();
    public string[] BaseDependentNames { get; init; } = Array.Empty<string>();
    public string? OffsetsFile { get; init; }
    public string[] BuildScripts { get; init; } = { "BuildLinked.ps1", "Publish.ps1" };
    public string Pipeline { get; init; } = "tools/pipeline.ps1";
    public string? PublishScript { get; init; } = "Publish.ps1";
    public string Workflow { get; init; } = ".github/workflows/release.yml";
    public string[] PrunedDirs { get; init; } = Array.Empty<string>();
    public string[] PythonProd { get; init; } = Array.Empty<string>();
    public string[] EmDashScope { get; init; } = Array.Empty<string>();

    private static Rules? _loaded;

    public static Rules Load() => _loaded ??= Read(RepoRoot.Locate());

    public string ProdDir => Path.Combine(RepoRoot.Locate(), Prod.TrimEnd('/'));
    public string TestsDir => Path.Combine(RepoRoot.Locate(), Tests.TrimEnd('/'));
    public string ProdName => Prod.TrimEnd('/');
    public string BaselinePath(string file) => Path.Combine(RepoRoot.Locate(), Baselines, file);

    private static Rules Read(string root)
    {
        string json = File.ReadAllText(Path.Combine(root, "rules.json"));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var rules = JsonSerializer.Deserialize<Rules>(json, options) ?? throw new InvalidDataException("rules.json is empty");
        if (rules.Prod.Length == 0 || rules.Tests.Length == 0 || rules.TicketPrefix.Length == 0)
            throw new InvalidDataException("rules.json needs prod, tests and ticketPrefix");
        return rules;
    }
}
