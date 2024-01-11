﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.ProjectEngineHost;
using Microsoft.AspNetCore.Razor.ProjectSystem;
using Microsoft.AspNetCore.Razor.Telemetry;
using Microsoft.AspNetCore.Razor.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.AspNetCore.Razor.LanguageServer.Cohost;

internal class CohostProjectSnapshot : IProjectSnapshot
{
    private static readonly EmptyProjectEngineFactory s_fallbackProjectEngineFactory = new();
    private static readonly (IProjectEngineFactory Value, ICustomProjectEngineFactoryMetadata)[] s_projectEngineFactories = ProjectEngineFactories.Factories.Select(f => (f.Item1.Value, f.Item2)).ToArray();

    private readonly Project _project;
    private readonly DocumentSnapshotFactory _documentSnapshotFactory;
    private readonly ITelemetryReporter _telemetryReporter;
    private readonly ProjectKey _projectKey;
    private readonly Lazy<RazorConfiguration> _lazyConfiguration;
    private readonly Lazy<RazorProjectEngine> _lazyProjectEngine;
    private readonly AsyncLazy<ImmutableArray<TagHelperDescriptor>> _tagHelpersLazy;

    public CohostProjectSnapshot(Project project, DocumentSnapshotFactory documentSnapshotFactory, ITelemetryReporter telemetryReporter, JoinableTaskContext joinableTaskContext)
    {
        _project = project;
        _documentSnapshotFactory = documentSnapshotFactory;
        _telemetryReporter = telemetryReporter;
        _projectKey = ProjectKey.From(_project)!.Value;

        _lazyConfiguration = new Lazy<RazorConfiguration>(CreateRazorConfiguration);
        _lazyProjectEngine = new Lazy<RazorProjectEngine>(() => DefaultProjectEngineFactory.Create(
            Configuration,
            fileSystem: RazorProjectFileSystem.Create(Path.GetDirectoryName(FilePath)),
            configure: builder =>
            {
                builder.SetRootNamespace(RootNamespace);
                builder.SetCSharpLanguageVersion(CSharpLanguageVersion);
                builder.SetSupportLocalizedComponentNames();
            },
            fallback: s_fallbackProjectEngineFactory,
            factories: s_projectEngineFactories));

        _tagHelpersLazy = new AsyncLazy<ImmutableArray<TagHelperDescriptor>>(() =>
        {
            var resolver = new CompilationTagHelperResolver(_telemetryReporter);
            return resolver.GetTagHelpersAsync(_project, GetProjectEngine(), CancellationToken.None).AsTask();
        }, joinableTaskContext.Factory);
    }

    public ProjectKey Key => _projectKey;

    public RazorConfiguration Configuration => _lazyConfiguration.Value;

    public IEnumerable<string> DocumentFilePaths
        => _project.AdditionalDocuments
            .Where(d => d.FilePath!.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || d.FilePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.FilePath.AssumeNotNull());

    public string FilePath => _project.FilePath!;

    public string IntermediateOutputPath => FilePathNormalizer.GetNormalizedDirectoryName(_project.CompilationOutputInfo.AssemblyPath);

    public string? RootNamespace => _project.DefaultNamespace ?? "ASP";

    public string DisplayName => _project.Name;

    public VersionStamp Version => _project.Version;

    public LanguageVersion CSharpLanguageVersion => ((CSharpParseOptions)_project.ParseOptions!).LanguageVersion;

    public ImmutableArray<TagHelperDescriptor> TagHelpers => _tagHelpersLazy.GetValue();

    public ProjectWorkspaceState ProjectWorkspaceState => ProjectWorkspaceState.Create(TagHelpers, CSharpLanguageVersion);

    public IDocumentSnapshot? GetDocument(string filePath)
    {
        var textDocument = _project.AdditionalDocuments.FirstOrDefault(d => d.FilePath == filePath);
        if (textDocument is null)
        {
            return null;
        }

        return _documentSnapshotFactory.GetOrCreate(textDocument);
    }

    public RazorProjectEngine GetProjectEngine() => _lazyProjectEngine.Value;

    public ImmutableArray<IDocumentSnapshot> GetRelatedDocuments(IDocumentSnapshot document)
    {
        throw new NotImplementedException();
    }

    public bool IsImportDocument(IDocumentSnapshot document)
    {
        throw new NotImplementedException();
    }

    private RazorConfiguration CreateRazorConfiguration()
    {
        // See RazorSourceGenerator.RazorProviders.cs

        var globalOptions = _project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;

        globalOptions.TryGetValue("build_property.RazorConfiguration", out var configurationName);

        configurationName ??= "MVC-3.0"; // TODO: Source generator uses "default" here??

        if (!globalOptions.TryGetValue("build_property.RazorLangVersion", out var razorLanguageVersionString) ||
            !RazorLanguageVersion.TryParse(razorLanguageVersionString, out var razorLanguageVersion))
        {
            razorLanguageVersion = RazorLanguageVersion.Latest;
        }

        return RazorConfiguration.Create(razorLanguageVersion, configurationName, Enumerable.Empty<RazorExtension>(), useConsolidatedMvcViews: true);
    }
}
