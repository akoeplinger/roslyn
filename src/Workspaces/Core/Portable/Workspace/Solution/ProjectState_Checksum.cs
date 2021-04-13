﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Serialization;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    internal partial class ProjectState
    {
        public bool TryGetStateChecksums(out ProjectStateChecksums stateChecksums)
            => _lazyChecksums.TryGetValue(out stateChecksums);

        public Task<ProjectStateChecksums> GetStateChecksumsAsync(CancellationToken cancellationToken)
            => _lazyChecksums.GetValueAsync(cancellationToken);

        public async Task<Checksum> GetChecksumAsync(CancellationToken cancellationToken)
        {
            var collection = await _lazyChecksums.GetValueAsync(cancellationToken).ConfigureAwait(false);
            return collection.Checksum;
        }

        public Checksum GetParseOptionsChecksum()
            => GetParseOptionsChecksum(_solutionServices.Workspace.Services.GetService<ISerializerService>());

        private Checksum GetParseOptionsChecksum(ISerializerService serializer)
            => this.SupportsCompilation
                ? ChecksumCache.GetOrCreate(this.ParseOptions, _ => serializer.CreateParseOptionsChecksum(this.ParseOptions))
                : Checksum.Null;

        private async Task<ProjectStateChecksums> ComputeChecksumsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (Logger.LogBlock(FunctionId.ProjectState_ComputeChecksumsAsync, FilePath, cancellationToken))
                {
                    var documentChecksumsTasks = DocumentStates.SelectAsArray(static (state, token) => state.GetChecksumAsync(token), cancellationToken);
                    var additionalDocumentChecksumTasks = AdditionalDocumentStates.SelectAsArray(static (state, token) => state.GetChecksumAsync(token), cancellationToken);
                    var analyzerConfigDocumentChecksumTasks = AnalyzerConfigDocumentStates.SelectAsArray(static (state, token) => state.GetChecksumAsync(token), cancellationToken);

                    var serializer = _solutionServices.Workspace.Services.GetService<ISerializerService>();

                    var infoChecksum = serializer.CreateChecksum(ProjectInfo.Attributes, cancellationToken);

                    // these compiler objects doesn't have good place to cache checksum. but rarely ever get changed.
                    var compilationOptionsChecksum = SupportsCompilation ? ChecksumCache.GetOrCreate(CompilationOptions, _ => serializer.CreateChecksum(CompilationOptions, cancellationToken)) : Checksum.Null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var parseOptionsChecksum = GetParseOptionsChecksum(serializer);

                    var projectReferenceChecksums = ChecksumCache.GetOrCreate<ProjectReferenceChecksumCollection>(ProjectReferences, _ => new ProjectReferenceChecksumCollection(ProjectReferences.Select(r => serializer.CreateChecksum(r, cancellationToken)).ToArray()));
                    var metadataReferenceChecksums = ChecksumCache.GetOrCreate<MetadataReferenceChecksumCollection>(MetadataReferences, _ => new MetadataReferenceChecksumCollection(MetadataReferences.Select(r => serializer.CreateChecksum(r, cancellationToken)).ToArray()));
                    var analyzerReferenceChecksums = ChecksumCache.GetOrCreate<AnalyzerReferenceChecksumCollection>(AnalyzerReferences, _ => new AnalyzerReferenceChecksumCollection(AnalyzerReferences.Select(r => serializer.CreateChecksum(r, cancellationToken)).ToArray()));

                    var documentChecksums = await Task.WhenAll(documentChecksumsTasks).ConfigureAwait(false);
                    var additionalChecksums = await Task.WhenAll(additionalDocumentChecksumTasks).ConfigureAwait(false);
                    var analyzerConfigDocumentChecksums = await Task.WhenAll(analyzerConfigDocumentChecksumTasks).ConfigureAwait(false);

                    return new ProjectStateChecksums(
                        infoChecksum,
                        compilationOptionsChecksum,
                        parseOptionsChecksum,
                        new DocumentChecksumCollection(documentChecksums),
                        projectReferenceChecksums,
                        metadataReferenceChecksums,
                        analyzerReferenceChecksums,
                        new TextDocumentChecksumCollection(additionalChecksums),
                        new AnalyzerConfigDocumentChecksumCollection(analyzerConfigDocumentChecksums));
                }
            }
            catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e, cancellationToken))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }
    }
}
