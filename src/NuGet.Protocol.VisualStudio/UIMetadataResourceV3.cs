﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.v3;
using NuGet.Protocol.Core.v3.Data;
using NuGet.Versioning;

namespace NuGet.Protocol.VisualStudio
{
    public class UIMetadataResourceV3 : UIMetadataResource
    {
        private readonly RegistrationResourceV3 _regResource;
        private readonly ReportAbuseResourceV3 _reportAbuseResource;
        private readonly DataClient _client;

        public UIMetadataResourceV3(DataClient client, RegistrationResourceV3 regResource, ReportAbuseResourceV3 reportAbuseResource)
            : base()
        {
            _regResource = regResource;
            _client = client;
            _reportAbuseResource = reportAbuseResource;
        }

        public override async Task<IEnumerable<UIPackageMetadata>> GetMetadata(IEnumerable<PackageIdentity> packages, CancellationToken token)
        {
            var results = new List<UIPackageMetadata>();

            // group by id to optimize
            foreach (var group in packages.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
            {
                var versions = group.OrderBy(e => e.Version, VersionComparer.VersionRelease);

                // find the range of versions we need
                var range = new VersionRange(versions.First().Version, true, versions.Last().Version, true, true);

                var metadataList = await _regResource.GetPackageMetadata(group.Key, range, true, true, token);

                results.AddRange(metadataList.Select(item => ParseMetadata(item)));
            }

            return results;
        }

        public override async Task<IEnumerable<UIPackageMetadata>> GetMetadata(string packageId, bool includePrerelease, bool includeUnlisted, CancellationToken token)
        {
            var metadataList = await _regResource.GetPackageMetadata(packageId, includePrerelease, includeUnlisted, token);
            return metadataList.Select(item => ParseMetadata(item));
        }

        public UIPackageMetadata ParseMetadata(JObject metadata)
        {
            var version = NuGetVersion.Parse(metadata.Value<string>(Properties.Version));
            DateTimeOffset? published = null;
            var publishedToken = metadata[Properties.Published];
            if (publishedToken != null)
            {
                published = publishedToken.ToObject<DateTimeOffset>();
            }

            var downloadCountString = metadata.Value<string>(Properties.DownloadCount);

            int? downloadCountValue;
            int downloadCount;
            if (int.TryParse(
                downloadCountString,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out downloadCount))
            {
                downloadCountValue = downloadCount;
            }
            else
            {
                downloadCountValue = null;
            }

            var id = metadata.Value<string>(Properties.PackageId);
            var title = metadata.Value<string>(Properties.Title);
            var summary = metadata.Value<string>(Properties.Summary);
            var description = metadata.Value<string>(Properties.Description);
            var authors = GetField(metadata, Properties.Authors);
            var owners = GetField(metadata, Properties.Owners);
            var iconUrl = GetUri(metadata, Properties.IconUrl);
            var licenseUrl = GetUri(metadata, Properties.LicenseUrl);
            var projectUrl = GetUri(metadata, Properties.ProjectUrl);
            var tags = GetField(metadata, Properties.Tags);
            var dependencySets = (metadata.Value<JArray>(Properties.DependencyGroups) ?? Enumerable.Empty<JToken>()).Select(obj => LoadDependencySet((JObject)obj));
            var requireLicenseAcceptance = metadata[Properties.RequireLicenseAcceptance] == null ? false : metadata[Properties.RequireLicenseAcceptance].ToObject<bool>();

            var reportAbuseUrl =
                _reportAbuseResource != null ?
                    _reportAbuseResource.GetReportAbuseUrl(id, version) :
                    null;

            if (String.IsNullOrEmpty(title))
            {
                // If no title exists, use the Id
                title = id;
            }

            return new UIPackageMetadata(
                new PackageIdentity(id, version),
                title,
                summary,
                description,
                authors,
                owners,
                iconUrl,
                licenseUrl,
                projectUrl,
                reportAbuseUrl,
                tags,
                published,
                dependencySets,
                requireLicenseAcceptance,
                downloadCountValue);
        }

        /// <summary>
        /// Returns a field value or the empty string. Arrays will become comma delimited strings.
        /// </summary>
        private static string GetField(JObject json, string property)
        {
            var value = json[property];

            if (value == null)
            {
                return string.Empty;
            }

            var array = value as JArray;

            if (array != null)
            {
                return String.Join(", ", array.Select(e => e.ToString()));
            }

            return value.ToString();
        }

        private Uri GetUri(JObject json, string property)
        {
            if (json[property] == null)
            {
                return null;
            }
            var str = json[property].ToString();
            if (String.IsNullOrEmpty(str))
            {
                return null;
            }
            return new Uri(str);
        }

        private static PackageDependencyGroup LoadDependencySet(JObject set)
        {
            var fxName = set.Value<string>(Properties.TargetFramework);

            var framework = NuGetFramework.AnyFramework;

            if (!String.IsNullOrEmpty(fxName))
            {
                framework = NuGetFramework.Parse(fxName);
                fxName = framework.GetShortFolderName();
            }

            return new PackageDependencyGroup(framework,
                (set.Value<JArray>(Properties.Dependencies) ?? Enumerable.Empty<JToken>()).Select(obj => LoadDependency((JObject)obj)));
        }

        private static Packaging.Core.PackageDependency LoadDependency(JObject dep)
        {
            var ver = dep.Value<string>(Properties.Range);
            return new Packaging.Core.PackageDependency(
                dep.Value<string>(Properties.PackageId),
                String.IsNullOrEmpty(ver) ? null : VersionRange.Parse(ver));
        }
    }
}
