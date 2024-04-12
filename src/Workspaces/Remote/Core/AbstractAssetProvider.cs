﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Serialization;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Remote;

/// <summary>
/// Provides corresponding data of the given checksum
/// </summary>
internal abstract class AbstractAssetProvider
{
    /// <summary>
    /// return data of type T whose checksum is the given checksum
    /// </summary>
    public abstract ValueTask<T> GetAssetAsync<T>(AssetPath assetPath, Checksum checksum, CancellationToken cancellationToken);
    public abstract Task GetAssetsAsync<T, TArg>(AssetPath assetPath, HashSet<Checksum> checksums, Action<Checksum, T, TArg>? callback, TArg? arg, CancellationToken cancellationToken);

    public async Task<SolutionInfo> CreateSolutionInfoAsync(Checksum solutionChecksum, CancellationToken cancellationToken)
    {
        var solutionCompilationChecksums = await GetAssetAsync<SolutionCompilationStateChecksums>(AssetPathKind.SolutionCompilationStateChecksums, solutionChecksum, cancellationToken).ConfigureAwait(false);
        var solutionChecksums = await GetAssetAsync<SolutionStateChecksums>(AssetPathKind.SolutionStateChecksums, solutionCompilationChecksums.SolutionState, cancellationToken).ConfigureAwait(false);

        var solutionAttributes = await GetAssetAsync<SolutionInfo.SolutionAttributes>(AssetPathKind.SolutionAttributes, solutionChecksums.Attributes, cancellationToken).ConfigureAwait(false);
        await GetAssetAsync<SourceGeneratorExecutionVersionMap>(AssetPathKind.SolutionSourceGeneratorExecutionVersionMap, solutionCompilationChecksums.SourceGeneratorExecutionVersionMap, cancellationToken).ConfigureAwait(false);

        // Fetch all the project state checksums up front.  That allows getting all the data in a single call, and
        // enables parallel fetching of the projects below.
        using var _1 = ArrayBuilder<Task<ProjectInfo>>.GetInstance(solutionChecksums.Projects.Length, out var projectsTasks);
        await this.GetAssetHelper<ProjectStateChecksums>().GetAssetsAsync(
            AssetPathKind.ProjectStateChecksums,
            solutionChecksums.Projects.Checksums,
            static (_, projectStateChecksums, tuple) => tuple.projectsTasks.Add(tuple.@this.CreateProjectInfoAsync(projectStateChecksums, tuple.cancellationToken)),
            (@this: this, projectsTasks, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var analyzerReferences = await this.GetAssetsArrayAsync<AnalyzerReference>(AssetPathKind.SolutionAnalyzerReferences, solutionChecksums.AnalyzerReferences, cancellationToken).ConfigureAwait(false);

        // Fetch the projects in parallel.
        var projects = await Task.WhenAll(projectsTasks).ConfigureAwait(false);
        return SolutionInfo.Create(
            solutionAttributes.Id,
            solutionAttributes.Version,
            solutionAttributes.FilePath,
            ImmutableCollectionsMarshal.AsImmutableArray(projects),
            analyzerReferences).WithTelemetryId(solutionAttributes.TelemetryId);
    }

    public async Task<ProjectInfo> CreateProjectInfoAsync(ProjectStateChecksums projectChecksums, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var projectId = projectChecksums.ProjectId;

        var attributes = await GetAssetAsync<ProjectInfo.ProjectAttributes>(new(AssetPathKind.ProjectAttributes, projectId), projectChecksums.Info, cancellationToken).ConfigureAwait(false);
        Contract.ThrowIfFalse(RemoteSupportedLanguages.IsSupported(attributes.Language));

        var compilationOptions = attributes.FixUpCompilationOptions(
            await GetAssetAsync<CompilationOptions>(new(AssetPathKind.ProjectCompilationOptions, projectId), projectChecksums.CompilationOptions, cancellationToken).ConfigureAwait(false));
        var parseOptionsTask = GetAssetAsync<ParseOptions>(new(AssetPathKind.ProjectParseOptions, projectId), projectChecksums.ParseOptions, cancellationToken);

        var projectReferencesTask = this.GetAssetsArrayAsync<ProjectReference>(new(AssetPathKind.ProjectProjectReferences, projectId), projectChecksums.ProjectReferences, cancellationToken);
        var metadataReferencesTask = this.GetAssetsArrayAsync<MetadataReference>(new(AssetPathKind.ProjectMetadataReferences, projectId), projectChecksums.MetadataReferences, cancellationToken);
        var analyzerReferencesTask = this.GetAssetsArrayAsync<AnalyzerReference>(new(AssetPathKind.ProjectAnalyzerReferences, projectId), projectChecksums.AnalyzerReferences, cancellationToken);

        // Attempt to fetch all the documents for this project in bulk.  This will allow for all the data to be fetched
        // efficiently.  We can then go and create the DocumentInfos for each document in the project.
        await SynchronizeProjectDocumentsAsync(projectChecksums, cancellationToken).ConfigureAwait(false);

        var documentInfosTask = CreateDocumentInfosAsync(projectChecksums.Documents);
        var additionalDocumentInfosTask = CreateDocumentInfosAsync(projectChecksums.AdditionalDocuments);
        var analyzerConfigDocumentInfosTask = CreateDocumentInfosAsync(projectChecksums.AnalyzerConfigDocuments);

        return ProjectInfo.Create(
            attributes,
            compilationOptions,
            await parseOptionsTask.ConfigureAwait(false),
            await documentInfosTask.ConfigureAwait(false),
            await projectReferencesTask.ConfigureAwait(false),
            await metadataReferencesTask.ConfigureAwait(false),
            await analyzerReferencesTask.ConfigureAwait(false),
            await additionalDocumentInfosTask.ConfigureAwait(false),
            await analyzerConfigDocumentInfosTask.ConfigureAwait(false),
            hostObjectType: null); // TODO: https://github.com/dotnet/roslyn/issues/62804

        async Task<ImmutableArray<DocumentInfo>> CreateDocumentInfosAsync(DocumentChecksumsAndIds checksumsAndIds)
        {
            var documentInfos = new DocumentInfo[checksumsAndIds.Length];

            var index = 0;
            foreach (var (attributeChecksum, textChecksum, documentId) in checksumsAndIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                documentInfos[index++] = await CreateDocumentInfoAsync(documentId, attributeChecksum, textChecksum, cancellationToken).ConfigureAwait(false);
            }

            return ImmutableCollectionsMarshal.AsImmutableArray(documentInfos);
        }
    }

    public async Task SynchronizeProjectDocumentsAsync(
        ProjectStateChecksums projectChecksums, CancellationToken cancellationToken)
    {
        await Task.Yield();

        using var _1 = PooledHashSet<Checksum>.GetInstance(out var attributeChecksums);
        using var _2 = PooledHashSet<Checksum>.GetInstance(out var textChecksums);

        projectChecksums.Documents.AttributeChecksums.AddAllTo(attributeChecksums);
        projectChecksums.AdditionalDocuments.AttributeChecksums.AddAllTo(attributeChecksums);
        projectChecksums.AnalyzerConfigDocuments.AttributeChecksums.AddAllTo(attributeChecksums);

        projectChecksums.Documents.TextChecksums.AddAllTo(textChecksums);
        projectChecksums.AdditionalDocuments.TextChecksums.AddAllTo(textChecksums);
        projectChecksums.AnalyzerConfigDocuments.TextChecksums.AddAllTo(textChecksums);

        var attributesTask = this.GetAssetsAsync<DocumentInfo.DocumentAttributes>(
            assetPath: new(AssetPathKind.DocumentAttributes, projectChecksums.ProjectId),
            attributeChecksums,
            cancellationToken);

        var textTask = this.GetAssetsAsync<SerializableSourceText>(
            assetPath: new(AssetPathKind.DocumentText, projectChecksums.ProjectId),
            textChecksums,
            cancellationToken);

        await Task.WhenAll(attributesTask, textTask).ConfigureAwait(false);
    }

    public async Task<DocumentInfo> CreateDocumentInfoAsync(
        DocumentId documentId, Checksum attributeChecksum, Checksum textChecksum, CancellationToken cancellationToken)
    {
        var attributes = await GetAssetAsync<DocumentInfo.DocumentAttributes>(new(AssetPathKind.DocumentAttributes, documentId), attributeChecksum, cancellationToken).ConfigureAwait(false);
        var serializableSourceText = await GetAssetAsync<SerializableSourceText>(new(AssetPathKind.DocumentText, documentId), textChecksum, cancellationToken).ConfigureAwait(false);

        var text = await serializableSourceText.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var textLoader = TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), attributes.FilePath));

        // TODO: do we need version?
        return new DocumentInfo(attributes, textLoader, documentServiceProvider: null);
    }

    public AssetHelper<T> GetAssetHelper<T>()
        => new(this);

    public readonly struct AssetHelper<T>(AbstractAssetProvider assetProvider)
    {
        public Task GetAssetsAsync<TArg>(AssetPath assetPath, HashSet<Checksum> checksums, Action<Checksum, T, TArg>? callback, TArg? arg, CancellationToken cancellationToken)
            => assetProvider.GetAssetsAsync(assetPath, checksums, callback, arg, cancellationToken);

        public Task GetAssetsAsync<TArg>(AssetPath assetPath, ChecksumCollection checksums, Action<Checksum, T, TArg>? callback, TArg? arg, CancellationToken cancellationToken)
            => assetProvider.GetAssetsAsync(assetPath, checksums, callback, arg, cancellationToken);
    }
}

internal static class AbstractAssetProviderExtensions
{
    public static Task GetAssetsAsync<TAsset>(
        this AbstractAssetProvider assetProvider, AssetPath assetPath, HashSet<Checksum> checksums, CancellationToken cancellationToken)
    {
        return assetProvider.GetAssetsAsync<TAsset, VoidResult>(
            assetPath, checksums, callback: null, arg: default, cancellationToken);
    }

    public static Task GetAssetsAsync<T>(
        this AbstractAssetProvider assetProvider, AssetPath assetPath, ChecksumCollection checksums, CancellationToken cancellationToken)
    {
        return assetProvider.GetAssetsAsync<T, VoidResult>(
            assetPath, checksums, callback: null, arg: default, cancellationToken);
    }

    public static async Task GetAssetsAsync<T, TArg>(
        this AbstractAssetProvider assetProvider, AssetPath assetPath, ChecksumCollection checksums, Action<Checksum, T, TArg>? callback, TArg? arg, CancellationToken cancellationToken)
    {
        using var _1 = PooledHashSet<Checksum>.GetInstance(out var checksumSet);
#if NET
        checksumSet.EnsureCapacity(checksums.Children.Length);
#endif
        checksumSet.AddAll(checksums.Children);

        await assetProvider.GetAssetsAsync(assetPath, checksumSet, callback, arg, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ImmutableArray<T>> GetAssetsArrayAsync<T>(
        this AbstractAssetProvider assetProvider, AssetPath assetPath, ChecksumCollection checksums, CancellationToken cancellationToken) where T : class
    {
        using var _1 = PooledHashSet<Checksum>.GetInstance(out var checksumSet);
        checksumSet.AddAll(checksums.Children);

        var builder = ImmutableArray.CreateBuilder<T>(checksumSet.Count);

        await assetProvider.GetAssetHelper<T>().GetAssetsAsync(
            assetPath, checksumSet,
            static (checksum, asset, builder) => builder.Add(asset),
            builder,
            cancellationToken).ConfigureAwait(false);

        return builder.MoveToImmutable();
    }
}
