﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Packaging.Core;
using ZipFilePair = System.Tuple<string, System.IO.Compression.ZipArchiveEntry>;

namespace NuGet.Packaging
{
    public static class PackageExtractor
    {
        public static async Task<IEnumerable<string>> ExtractPackageAsync(Stream packageStream, PackageIdentity packageIdentity,
            PackagePathResolver packagePathResolver,
            PackageExtractionContext packageExtractionContext,
            PackageSaveModes packageSaveMode,
            CancellationToken token)
        {
            var filesAdded = new List<string>();
            if (packageStream == null)
            {
                throw new ArgumentNullException("packageStream");
            }

            if (!packageStream.CanSeek)
            {
                throw new ArgumentException(Strings.PackageStreamShouldBeSeekable);
            }

            if (packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if (packagePathResolver == null)
            {
                throw new ArgumentNullException("packagePathResolver");
            }

            // TODO: Need to handle PackageSaveMode
            // TODO: Support overwriting files also?
            var nupkgStartPosition = packageStream.Position;
            var zipArchive = new ZipArchive(packageStream);

            // default to non-legacy paths
            var useLegacyPaths = packageExtractionContext == null ? false : packageExtractionContext.UseLegacyPackageInstallPath;

            var packageReader = new PackageReader(zipArchive);
            var nuspecReader = new NuspecReader(packageReader.GetNuspec());
            var packageVersionFromNuspec = nuspecReader.GetVersion();

            var packageDirectoryInfo = Directory.CreateDirectory(packagePathResolver.GetInstallPath(new PackageIdentity(packageIdentity.Id, packageVersionFromNuspec), useLegacyPaths));
            var packageDirectory = packageDirectoryInfo.FullName;

            filesAdded.AddRange(await PackageHelper.CreatePackageFiles(zipArchive.Entries, packageDirectory, packageSaveMode, token));

            var nupkgFilePath = Path.Combine(packageDirectory, packagePathResolver.GetPackageFileName(packageIdentity));
            if (packageSaveMode.HasFlag(PackageSaveModes.Nupkg))
            {
                // During package extraction, nupkg is the last file to be created
                // Since all the packages are already created, the package stream is likely positioned at its end
                // Reset it to the nupkgStartPosition
                packageStream.Seek(nupkgStartPosition, SeekOrigin.Begin);
                filesAdded.Add(await PackageHelper.CreatePackageFile(nupkgFilePath, packageStream, token));
            }

            // Now, copy satellite files unless requested to not copy them
            if (packageExtractionContext == null
                || packageExtractionContext.CopySatelliteFiles)
            {
                filesAdded.AddRange(await CopySatelliteFilesAsync(packageIdentity, packagePathResolver, packageSaveMode, token));
            }

            return filesAdded;
        }

        public static async Task<IEnumerable<string>> CopySatelliteFilesAsync(PackageIdentity packageIdentity, PackagePathResolver packagePathResolver,
            PackageSaveModes packageSaveMode, CancellationToken token)
        {
            var satelliteFilesCopied = Enumerable.Empty<string>();
            if (packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if (packagePathResolver == null)
            {
                throw new ArgumentNullException("packagePathResolver");
            }

            var nupkgFilePath = packagePathResolver.GetInstalledPackageFilePath(packageIdentity);
            if (File.Exists(nupkgFilePath))
            {
                using (var packageStream = File.OpenRead(nupkgFilePath))
                {
                    string language;
                    string runtimePackageDirectory;
                    IEnumerable<ZipArchiveEntry> satelliteFiles;
                    if (PackageHelper.GetSatelliteFiles(packageStream, packageIdentity, packagePathResolver, out language, out runtimePackageDirectory, out satelliteFiles))
                    {
                        // Now, add all the satellite files collected from the package to the runtime package folder(s)
                        satelliteFilesCopied = await PackageHelper.CreatePackageFiles(satelliteFiles, runtimePackageDirectory, packageSaveMode, token);
                    }
                }
            }

            return satelliteFilesCopied;
        }
    }
}
