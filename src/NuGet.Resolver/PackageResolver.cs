﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NuGet.Packaging.Core;

namespace NuGet.Resolver
{
    /// <summary>
    /// A core package dependency resolver.
    /// </summary>
    /// <remarks>Not thread safe</remarks>
    public class PackageResolver
    {
        /// <summary>
        /// Resolve a package closure
        /// </summary>
        public IEnumerable<PackageIdentity> Resolve(PackageResolverContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // validation 
            foreach (var requiredId in context.RequiredPackageIds)
            {
                if (!context.AvailablePackages.Any(p => StringComparer.OrdinalIgnoreCase.Equals(p.Id, requiredId)))
                {
                    throw new NuGetResolverInputException(String.Format(CultureInfo.CurrentCulture, Strings.MissingDependencyInfo, requiredId));
                }
            }

            // convert the available packages into ResolverPackages
            var resolverPackages = new List<ResolverPackage>();

            foreach (var package in context.AvailablePackages)
            {
                IEnumerable<PackageDependency> dependencies = null;

                // clear out the dependencies if the behavior is set to ignore
                if (context.DependencyBehavior == DependencyBehavior.Ignore)
                {
                    dependencies = Enumerable.Empty<PackageDependency>();
                }
                else
                {
                    dependencies = package.Dependencies ?? Enumerable.Empty<PackageDependency>();
                }

                resolverPackages.Add(new ResolverPackage(package.Id, package.Version, dependencies, package.Listed, false));
            }

            // Sort the packages to make this process as deterministic as possible
            resolverPackages.Sort(PackageIdentityComparer.Default);

            // Keep track of the ids we have added
            var groupsAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var grouped = new List<List<ResolverPackage>>();

            // group the packages by id
            foreach (var group in resolverPackages.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
            {
                groupsAdded.Add(group.Key);

                var curSet = group.ToList();

                // add an absent package for non-targets
                // being absent allows the resolver to throw it out if it is not needed
                if (!context.RequiredPackageIds.Contains(group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    curSet.Add(new ResolverPackage(id: group.Key, version: null, dependencies: null, listed: true, absent: true));
                }

                grouped.Add(curSet);
            }

            // find all needed dependencies
            var dependencyIds = resolverPackages.Where(e => e.Dependencies != null)
                .SelectMany(e => e.Dependencies.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase));

            foreach (string depId in dependencyIds)
            {
                // packages which are unavailable need to be added as absent packages
                // ex: if A -> B  and B is not found anywhere in the source repositories we add B as absent
                if (!groupsAdded.Contains(depId))
                {
                    groupsAdded.Add(depId);
                    grouped.Add(new List<ResolverPackage>() { new ResolverPackage(id: depId, version: null, dependencies: null, listed: true, absent: true) });
                }
            }

            token.ThrowIfCancellationRequested();

            // keep track of the best partial solution
            var bestSolution = Enumerable.Empty<ResolverPackage>();

            Action<IEnumerable<ResolverPackage>> diagnosticOutput = (partialSolution) =>
            {
                // store each solution as they pass through.
                // the combination solver verifies that the last one returned is the best
                bestSolution = partialSolution;
            };

            // Run solver
            var comparer = new ResolverComparer(context.DependencyBehavior, context.PreferredVersions, context.TargetIds);

            var solution = CombinationSolver<ResolverPackage>.FindSolution(
                groupedItems: grouped,
                itemSorter: comparer,
                shouldRejectPairFunc: ResolverUtility.ShouldRejectPackagePair,
                diagnosticOutput: diagnosticOutput);

            // check if a solution was found
            if (solution != null)
            {
                var nonAbsentCandidates = solution.Where(c => !c.Absent);

                if (nonAbsentCandidates.Any())
                {
                    var circularReferences = ResolverUtility.FindCircularDependency(solution);

                    if (circularReferences.Any())
                    {
                        // the resolver is able to handle circular dependencies, however we should throw here to keep these from happening
                        throw new NuGetResolverConstraintException(
                            String.Format(CultureInfo.CurrentCulture, Strings.CircularDependencyDetected,
                            String.Join(" => ", circularReferences.Select(package => $"{package.Id} {package.Version.ToNormalizedString()}"))));
                    }

                    // solution found!
                    var sortedSolution = ResolverUtility.TopologicalSort(nonAbsentCandidates);

                    return sortedSolution.ToArray();
                }
            }

            // no solution was found, throw an error with a diagnostic message
            var message = ResolverUtility.GetDiagnosticMessage(bestSolution, context.AvailablePackages, context.PackagesConfig, context.TargetIds);
            throw new NuGetResolverConstraintException(message);
        }
    }
}
