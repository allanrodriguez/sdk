// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Microsoft.AspNetCore.Razor.Tasks
{
    public class DiscoverStaticWebAssets : Task
    {
        [Required]
        public ITaskItem[] Candidates { get; set; }

        [Required]
        public string Pattern { get; set; }

        [Required]
        public string SourceId { get; set; }

        [Required]
        public string ContentRoot { get; set; }

        [Required]
        public string BasePath { get; set; }

        [Output]
        public ITaskItem[] DiscoveredStaticWebAssets { get; set; }

        public override bool Execute()
        {
            try
            {
                var matcher = new Matcher().AddInclude(Pattern);
                var assets = new List<ITaskItem>();
                var assetsByRelativePath = new Dictionary<string, List<ITaskItem>>();

                for (var i = 0; i < Candidates.Length; i++)
                {
                    var candidate = Candidates[i];
                    var candidateMatchPath = GetCandidateMatchPath(candidate);
                    var match = matcher.Match(candidateMatchPath);
                    if (!match.HasMatches)
                    {
                        Log.LogMessage("Rejected asset '{0}' for pattern '{1}'", candidateMatchPath, Pattern);
                        continue;
                    }

                    Log.LogMessage("Accepted asset '{0}' for pattern '{1}' with relative path '{2}'", candidateMatchPath, Pattern, match.Files.Single().Stem);

                    var candidateRelativePath = StaticWebAsset.Normalize(match.Files.Single().Stem);
                    var asset = new TaskItem(candidate.ItemSpec, new Dictionary<string, string>
                    {
                        [nameof(StaticWebAsset.SourceType)] = StaticWebAsset.SourceTypes.Discovered,
                        [nameof(StaticWebAsset.SourceId)] = SourceId,
                        [nameof(StaticWebAsset.ContentRoot)] = ContentRoot,
                        [nameof(StaticWebAsset.BasePath)] = StaticWebAsset.Normalize(BasePath),
                        [nameof(StaticWebAsset.RelativePath)] = candidateRelativePath,
                        [nameof(StaticWebAsset.AssetKind)] = StaticWebAsset.AssetKinds.All,
                        [nameof(StaticWebAsset.AssetMode)] = StaticWebAsset.AssetModes.All,
                        [nameof(StaticWebAsset.CopyToOutputDirectory)] = ComputeCopyOption(candidate.GetMetadata(nameof(StaticWebAsset.CopyToOutputDirectory)), StaticWebAsset.AssetCopyOptions.Never),
                        [nameof(StaticWebAsset.CopyToPublishDirectory)] = ComputeCopyOption(candidate.GetMetadata(nameof(StaticWebAsset.CopyToPublishDirectory)), StaticWebAsset.AssetCopyOptions.PreserveNewest),
                    });

                    assets.Add(asset);

                    UpdateAssetKindIfNecessary(assetsByRelativePath, candidateRelativePath, asset);
                    if (Log.HasLoggedErrors)
                    {
                        return false;
                    }
                }

                DiscoveredStaticWebAssets = assets.ToArray();
            }
            catch (Exception ex)
            {
                Log.LogError(ex.Message);
            }

            return !Log.HasLoggedErrors;
        }

        private string GetCandidateMatchPath(ITaskItem candidate)
        {
            var targetPath = candidate.GetMetadata("TargetPath");
            if (!string.IsNullOrEmpty(targetPath))
            {
                Log.LogMessage("TargetPath '{0}' found for candidate '{1}' and will be used for matching.", targetPath, candidate.ItemSpec);
                return targetPath;
            }

            var linkPath = candidate.GetMetadata("Link");
            if (!string.IsNullOrEmpty(linkPath))
            {
                Log.LogMessage("Link '{0}' found for candidate '{1}' and will be used for matching.", linkPath, candidate.ItemSpec);

                return linkPath;
            }

            return candidate.ItemSpec;
        }

        private void UpdateAssetKindIfNecessary(Dictionary<string, List<ITaskItem>> assetsByRelativePath, string candidateRelativePath, TaskItem asset)
        {
            // We want to support content items in the form of
            // <Content Include="service-worker.development.js CopyToPublishDirectory="Never" TargetPath="wwwroot\service-worker.js" />
            // <Content Include="service-worker.js />
            // where the first item is used during development and the second item is used when the app is published.
            // To that matter, we keep track of the assets relative paths and make sure that when two assets target the same relative paths, at least one
            // of them is marked with CopyToPublishDirectory="Never" to identify it as a "development/build" time asset as opposed to the other asset.
            // As a result, assets by default have an asset kind 'All' when there is only one asset for the target path and 'Build' or 'Publish' when there are two of them.
            if (!assetsByRelativePath.TryGetValue(candidateRelativePath, out var existing))
            {
                assetsByRelativePath.Add(candidateRelativePath, new List<ITaskItem> { asset });
            }
            else
            {
                if (existing.Count == 2)
                {
                    var first = existing[0];
                    var second = existing[1];
                    var errorMessage = "More than two assets are targeting the same path: " + Environment.NewLine +
                        "'{0}' with kind '{1}'" + Environment.NewLine +
                        "'{2}' with kind '{3}'" + Environment.NewLine +
                        "for path '{4}'";

                    Log.LogError(
                        errorMessage,
                        first.GetMetadata("FullPath"),
                        first.GetMetadata(nameof(StaticWebAsset.AssetKind)),
                        second.GetMetadata("FullPath"),
                        second.GetMetadata(nameof(StaticWebAsset.AssetKind)),
                        candidateRelativePath);

                    return;
                }
                else if (existing.Count == 1)
                {
                    var existingAsset = existing[0];
                    switch ((asset.GetMetadata(nameof(StaticWebAsset.CopyToPublishDirectory)), existingAsset.GetMetadata(nameof(StaticWebAsset.CopyToPublishDirectory))))
                    {
                        case (StaticWebAsset.AssetCopyOptions.Never, StaticWebAsset.AssetCopyOptions.Never):
                        case (not StaticWebAsset.AssetCopyOptions.Never, not StaticWebAsset.AssetCopyOptions.Never):
                            var errorMessage = "Two assets found targeting the same path with incompatible asset kinds: " + Environment.NewLine +
                                "'{0}' with kind '{1}'" + Environment.NewLine +
                                "'{2}' with kind '{3}'" + Environment.NewLine +
                                "for path '{4}'";
                            Log.LogError(
                                errorMessage,
                                existingAsset.GetMetadata("FullPath"),
                                existingAsset.GetMetadata(nameof(StaticWebAsset.AssetKind)),
                                asset.GetMetadata("FullPath"),
                                asset.GetMetadata(nameof(StaticWebAsset.AssetKind)),
                                candidateRelativePath);

                            break;

                        case (StaticWebAsset.AssetCopyOptions.Never, not StaticWebAsset.AssetCopyOptions.Never):
                            existing.Add(asset);
                            asset.SetMetadata(nameof(StaticWebAsset.AssetKind), StaticWebAsset.AssetKinds.Build);
                            existingAsset.SetMetadata(nameof(StaticWebAsset.AssetKind), StaticWebAsset.AssetKinds.Publish);
                            break;

                        case (not StaticWebAsset.AssetCopyOptions.Never, StaticWebAsset.AssetCopyOptions.Never):
                            existing.Add(asset);
                            asset.SetMetadata(nameof(StaticWebAsset.AssetKind), StaticWebAsset.AssetKinds.Publish);
                            existingAsset.SetMetadata(nameof(StaticWebAsset.AssetKind), StaticWebAsset.AssetKinds.Build);
                            break;
                    }
                }
            }
        }

        private static string ComputeCopyOption(string copyOption, string defaultValue) => string.Equals(copyOption, StaticWebAsset.AssetCopyOptions.Never) ? StaticWebAsset.AssetCopyOptions.Never :
                        string.Equals(copyOption, StaticWebAsset.AssetCopyOptions.PreserveNewest) ? StaticWebAsset.AssetCopyOptions.PreserveNewest :
                        string.Equals(copyOption, StaticWebAsset.AssetCopyOptions.Always) ? StaticWebAsset.AssetCopyOptions.Always :
                        defaultValue;
    }
}