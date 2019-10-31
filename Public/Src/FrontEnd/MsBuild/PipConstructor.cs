// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Reflection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Text;
using BuildXL.FrontEnd.Sdk;
using BuildXL.FrontEnd.Utilities;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.Native.Processes;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Qualifier;
using JetBrains.Annotations;
using static BuildXL.Utilities.FormattableStringEx;
using ProjectWithPredictions = BuildXL.FrontEnd.MsBuild.Serialization.ProjectWithPredictions<BuildXL.Utilities.AbsolutePath>;

namespace BuildXL.FrontEnd.MsBuild
{
    /// <summary>
    /// Creates a pip based on a <see cref="ProjectWithPredictions"/>
    /// </summary>
    internal sealed class PipConstructor
    {
        private readonly FrontEndContext m_context;
        private readonly ConcurrentDictionary<ProjectWithPredictions, MSBuildProjectOutputs> m_processOutputsPerProject = new ConcurrentDictionary<ProjectWithPredictions, MSBuildProjectOutputs>();
        
        // Only used if resolverSettings.EnableTransitiveProjectReferences = true
        private readonly ConcurrentBigMap<ProjectWithPredictions, IReadOnlySet<ProjectWithPredictions>> m_transitiveDependenciesPerProject = new ConcurrentBigMap<ProjectWithPredictions, IReadOnlySet<ProjectWithPredictions>>();

        private readonly FrontEndHost m_frontEndHost;
        private readonly ModuleDefinition m_moduleDefinition;

        private readonly IMsBuildResolverSettings m_resolverSettings;

        private static readonly string[] s_commonArgumentsToMsBuildExe =
        {
            "/NoLogo",
            "/p:TrackFileAccess=false", // Turns off MSBuild's Detours based tracking. Required always to prevent Detouring a Detour which is not supported
            "/m:1", // Tells MsBuild not to create child processes for building, but instead to simply execute the specified project file within a single process
            "/IgnoreProjectExtensions:.sln", // Tells MSBuild to avoid scanning the local file system for sln files to build, but instead to simply use the provided project file.
            "/ConsoleLoggerParameters:Verbosity=Minimal", // Minimize the console logger
            "/noAutoResponse", // do not include any MSBuild.rsp file automatically,
            "/nodeReuse:false" // Even though we are already passing /m:1, when an MSBuild task is requested with an architecture that doesn't match the one of the host process, /nodeReuse will be true unless set otherwise
        };

        // Keep in sync with the bxl deployment
        private const string VBCSCompilerLogger = "VBCSCompilerLogger.dll";

        private AbsolutePath Root => m_resolverSettings.Root;

        private readonly AbsolutePath m_msBuildPath; 
        private readonly AbsolutePath m_dotnetExePath;
        private readonly string m_frontEndName;
        private readonly IEnumerable<KeyValuePair<string, string>> m_userDefinedEnvironment;
        private readonly IEnumerable<string> m_userDefinedPassthroughVariables;
        
        private PathTable PathTable => m_context.PathTable;
        private FrontEndEngineAbstraction Engine => m_frontEndHost.Engine;

        /// <summary>
        /// The name of the output cache file of the given pip, if the corresponding process is built in isolation
        /// </summary>
        /// <remarks>
        /// This file is created in an object directory unique to this pip, so there is no need to make this name unique
        /// </remarks>
        internal const string OutputCacheFileName = "output.cache";

        // All projects should contain this property since the build graph is created by MSBuild under the /graph option
        // TODO: it would be better if MSBuild provided the property name
        internal const string s_isGraphBuildProperty = "IsGraphBuild";

        /// <nodoc/>
        public PipConstructor(
            FrontEndContext context,
            FrontEndHost frontEndHost,
            ModuleDefinition moduleDefinition,
            IMsBuildResolverSettings resolverSettings,
            AbsolutePath pathToMsBuild,
            AbsolutePath pathToDotnetExe,
            string frontEndName,
            IEnumerable<KeyValuePair<string, string>> userDefinedEnvironment,
            IEnumerable<string> userDefinedPassthroughVariables)
        {
            Contract.Requires(context != null);
            Contract.Requires(frontEndHost != null);
            Contract.Requires(moduleDefinition != null);
            Contract.Requires(resolverSettings != null);
            Contract.Requires(pathToMsBuild.IsValid);
            Contract.Requires(!resolverSettings.ShouldRunDotNetCoreMSBuild() || pathToDotnetExe.IsValid);
            Contract.Requires(!string.IsNullOrEmpty(frontEndName));
            Contract.Requires(userDefinedEnvironment != null);
            Contract.Requires(userDefinedPassthroughVariables != null);

            m_context = context;
            m_frontEndHost = frontEndHost;
            m_moduleDefinition = moduleDefinition;
            m_resolverSettings = resolverSettings;
            m_msBuildPath = pathToMsBuild;
            m_dotnetExePath = pathToDotnetExe;
            m_frontEndName = frontEndName;
            m_userDefinedEnvironment = userDefinedEnvironment;
            m_userDefinedPassthroughVariables = userDefinedPassthroughVariables;
        }

        /// <summary>
        /// Schedules a pip corresponding to the provided project and qualifier
        /// </summary>
        /// <remarks>
        /// The project is assumed to be scheduled in the right order, where all dependencies are scheduled first.
        /// See topographical sort performed in <see cref="PipGraphConstructor"/>.
        /// </remarks>
        public bool TrySchedulePipForFile(ProjectWithPredictions project, QualifierId qualifierId, out string failureDetail, out Process process)
        {
            try
            {
                // Create command line and inputs and outputs for pipBuilder.
                if (!TryExecuteArgumentsToPipBuilder(
                    project,
                    qualifierId,
                    out failureDetail,
                    out process))
                {
                    Tracing.Logger.Log.SchedulingPipFailure(
                        m_context.LoggingContext,
                        Location.FromFile(project.FullPath.ToString(PathTable)),
                        failureDetail);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Tracing.Logger.Log.UnexpectedPipBuilderException(
                    m_context.LoggingContext,
                    Location.FromFile(project.FullPath.ToString(PathTable)),
                    ex.GetLogEventMessage(),
                    ex.StackTrace);

                process = null;
                failureDetail = ex.ToString();

                return false;
            }

            return true;
        }

        private IReadOnlyDictionary<string, string> CreateEnvironment(AbsolutePath logDirectory, ProjectWithPredictions project)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            //
            // Initial environment variables that may be overwritten by the outer environment.
            //

            // With cpuCount set to 1 (/m:1 parameter to msbuild), MSBuild's synchronous logging
            // is in place. Relax it to async logging for a slight speedup.
            // Check UseSynchronousLogging on https://github.com/Microsoft/msbuild
            env[BuildEnvironmentConstants.MsBuildLogAsyncEnvVar] = "1";

            // Observe there is no need to inform the engine this environment is being used since
            // the same environment was used during graph construction, and the engine is already tracking them
            foreach (var input in m_userDefinedEnvironment)
            {
                string envVarName = input.Key;

                // Temp directory entries are added at pip creation time.
                if (string.Equals(envVarName, "TEMP", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(envVarName, "TMP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                env[envVarName] = input.Value;
            }

            //
            // Additional environment variables overlaid on top of the outer environment.
            //

            // Use a unique instance of mspdbsrv per project, for several reasons:
            // - We want to enable multi-proc cl and link as they're much faster than turning off /MP;
            //   mspdbsrv is required for multiproc to work.
            // - mspdbsrv by default wants to act as a machine-wide PDB server for linkers.
            //   This is the same as the Roslyn shared compiler service pattern,
            //   and collides with our need to have each project build in its own set of
            //   hermetic processes.
            // - Ability to use a job object to clean up the entire child process chain.
            // - mspdbsrv touches header files and other C++ artifacts from other build targets,
            //   which can cause them to appear as inputs in this target.
            // NOTE: Max 38 characters so we use a GUID with no braces or dashes.
            // TODO: The GUID is computed based on the project, which means that uniqueness is guaranteed
            // across this build, but this doesn't account for the case where multiple instances of BuildXL
            // may be running simultaneously. This is not possible today, but it might be in the near future.
            // The alternative is to use a fresh GUID and add it as a passthrough variable, but that lacks a feature
            // where the value of the variable is set for the process AND the variable is passthrough. At this point
            // a passthrough variable cannot be set explicitly.
            string mspdbsrvGuid = ComputeSha256(project);
            env[BuildEnvironmentConstants.MsPdbSrvEndpointEnvVar] = mspdbsrvGuid;

            // Enable MSBuild debugging if requested
            if (m_resolverSettings.EnableEngineTracing == true)
            {
                env[BuildEnvironmentConstants.MsBuildDebug] = "1";
                env[BuildEnvironmentConstants.MsBuildDebugPath] = logDirectory.ToString(PathTable);
            }

            return env;
        }

        private bool TryExecuteArgumentsToPipBuilder(
            ProjectWithPredictions project,
            QualifierId qualifierId,
            out string failureDetail,
            out Process scheduledProcess)
        {
            // We create a pip construction helper for each project
            var pipConstructionHelper = GetPipConstructionHelperForProject(project, qualifierId);

            using (var processBuilder = ProcessBuilder.Create(PathTable, m_context.GetPipDataBuilder()))
            {
                // Configure the process to add an assortment of settings: arguments, response file, etc.
                if (!TryConfigureProcessBuilder(processBuilder, pipConstructionHelper, project, out AbsolutePath outputResultCacheFile, out failureDetail))
                {
                    scheduledProcess = null;
                    return false;
                }

                // Process all predicted outputs and inputs, including the predicted project dependencies
                ProcessOutputs(project, processBuilder);
                ProcessInputs(project, processBuilder);

                // Try to schedule the process pip
                if (!pipConstructionHelper.TryAddProcess(processBuilder, out ProcessOutputs outputs, out scheduledProcess))
                {
                    failureDetail = "Failed to schedule the pip";
                    return false;
                }

                // Add the computed outputs for this project, so dependencies can consume it
                var outputDirectories = outputs.GetOutputDirectories();

                // A valid output cache path indicates that the project is building in isolation
                MSBuildProjectOutputs projectOutputs;
                if (outputResultCacheFile == AbsolutePath.Invalid)
                {
                    projectOutputs = MSBuildProjectOutputs.CreateLegacy(outputDirectories);
                }
                else
                {
                    var success = outputs.TryGetOutputFile(outputResultCacheFile, out FileArtifact cacheFileArtifact);
                    if (!success)
                    {
                        Contract.Assert(false, I($"The output cache file {outputResultCacheFile.ToString(PathTable)} should be part of the project {project.FullPath.ToString(PathTable)} outputs."));
                    }

                    projectOutputs = MSBuildProjectOutputs.CreateIsolated(outputDirectories, cacheFileArtifact);
                }
                
                // If the project is not implementing the target protocol, emit corresponding warn/verbose 
                if (!project.ImplementsTargetProtocol)
                {
                    if (project.ProjectReferences.Count != 0)
                    {
                        // Let's warn about this. Targets of the referenced projects may not be accurate
                        Tracing.Logger.Log.ProjectIsNotSpecifyingTheProjectReferenceProtocol(
                            m_context.LoggingContext,
                            Location.FromFile(project.FullPath.ToString(PathTable)),
                            project.FullPath.GetName(m_context.PathTable).ToString(m_context.StringTable));
                    }
                    else
                    { 
                        // Just a verbose message in this case
                        Tracing.Logger.Log.LeafProjectIsNotSpecifyingTheProjectReferenceProtocol(
                            m_context.LoggingContext,
                            Location.FromFile(project.FullPath.ToString(PathTable)),
                            project.FullPath.GetName(m_context.PathTable).ToString(m_context.StringTable));
                    }
                }

                // Warn if default targets were appended to the targets to execute
                if (project.PredictedTargetsToExecute.IsDefaultTargetsAppended)
                {
                    Tracing.Logger.Log.ProjectPredictedTargetsAlsoContainDefaultTargets(
                            m_context.LoggingContext,
                            Location.FromFile(project.FullPath.ToString(PathTable)),
                            project.FullPath.GetName(m_context.PathTable).ToString(m_context.StringTable),
                            $"[{string.Join(";", project.PredictedTargetsToExecute.AppendedDefaultTargets)}]");
                }

                m_processOutputsPerProject[project] = projectOutputs;

                failureDetail = string.Empty;
                return true;
            }
        }

        /// <summary>
        /// Adds all predicted dependencies as inputs, plus all individual inputs predicted for the project
        /// </summary>
        /// <remarks>
        /// Adding all predicted dependencies is key to get the right scheduling. On the other hand, all predicted inputs
        /// are not really needed since we are running in undeclared read mode. However, they contribute to make the weak fingerprint stronger (that
        /// otherwise will likely be just a shared opaque output at the root).
        /// </remarks>
        private void ProcessInputs(
            ProjectWithPredictions project, 
            ProcessBuilder processBuilder)
        {
            // Predicted output directories for all direct dependencies, plus the output directories for the given project itself
            var knownOutputDirectories = project.ProjectReferences.SelectMany(reference => reference.PredictedOutputFolders).Union(project.PredictedOutputFolders);

            var pkgRefGen = PathAtom.Create(PathTable.StringTable, ".pkgrefgen");

            // Add all predicted inputs that are recognized as true source files
            // This is done to make the weak fingerprint stronger. Pips are scheduled so undeclared source reads are allowed. This means
            // we don't actually need accurate (or in fact any) input predictions to run successfully. But we are trying to avoid the degenerate case
            // of a very small weak fingerprint with too many candidates, that can slow down two-phase cache look-up.
            foreach (AbsolutePath buildInput in project.PredictedInputFiles)
            {
                // If any of the predicted inputs is under the predicted output folder of a dependency, then there is a very good chance the predicted input is actually an intermediate file
                // In that case, don't add the input as a source file to stay on the safe side. Otherwise we will have a file that is both declared as a source file and contained in a directory
                // dependency.
                if (knownOutputDirectories.Any(outputFolder => buildInput.IsWithin(PathTable, outputFolder)))
                {
                    continue;
                }

                // Nuget restore does not produce deterministic files under the project .pkgrefgen folder, and many times they have absolute paths embedded. 
                // This blocks shared cache from working when the project file (and therefore the corresponding .pkgrefgen folder) is placed on machine-dependent folders
                // Even though untracking these generated files is not completely safe from a caching perspective, in practice this should be harmless since the generation
                // is controlled by files (e.g. project.assents.json or global.json) that are declared as inputs.
                // In this case, we not only skip declaring it as an input, but we also untrack it as well
                if (buildInput.GetParent(PathTable) is AbsolutePath parent && 
                    parent.IsValid && 
                    parent.GetName(PathTable) == pkgRefGen)
                {
                    processBuilder.AddUntrackedFile(buildInput);
                    continue;
                }

                // If any of the predicted inputs is under an untracked directory scope, don't add it as an input
                if (processBuilder.GetUntrackedDirectoryScopesSoFar().Any(untrackedDirectory => buildInput.IsWithin(PathTable, untrackedDirectory)))
                {
                    continue;
                }

                processBuilder.AddInputFile(FileArtifact.CreateSourceFile(buildInput));
            }

            IEnumerable<ProjectWithPredictions> references;

            // The default for EnableTransitiveProjectReferences is false, so it has to be true explicitly to kick in
            if (m_resolverSettings.EnableTransitiveProjectReferences == true)
            {
                // In this case all the transitive closure is automatically exposed to the project as direct references
                var transitiveReferences = new HashSet<ProjectWithPredictions>();
                ComputeTransitiveDependenciesFor(project, transitiveReferences);
                references = transitiveReferences;
            }
            else
            {
                // Only direct dependencies are declared. 
                // Add all known explicit inputs from project references. But rule out
                // projects that have a known empty list of targets: those projects are not scheduled, so
                // there is nothing to consume from them.
                references = project.ProjectReferences.Where(projectReference => projectReference.PredictedTargetsToExecute.Targets.Count != 0);
            }

            var argumentsBuilder = processBuilder.ArgumentsBuilder;
            foreach (ProjectWithPredictions projectReference in references)
            {
                bool outputsPresent = m_processOutputsPerProject.TryGetValue(projectReference, out MSBuildProjectOutputs projectOutputs);
                if (!outputsPresent)
                {
                    Contract.Assert(false, $"Pips must have been presented in dependency order: {projectReference.FullPath.ToString(PathTable)} missing, dependency of {project.FullPath.ToString(PathTable)}");
                }

                // Add all known output directories
                foreach (StaticDirectory output in projectOutputs.OutputDirectories)
                {
                    processBuilder.AddInputDirectory(output.Root);
                }

                // If the dependency was built in isolation, this project needs to access the generated cache files
                if (projectOutputs.BuildsInIsolation)
                {
                    var outputCache = projectOutputs.OutputCacheFile;
                    processBuilder.AddInputFile(outputCache);
                    // Instruct MSBuild to use the cache file from the associated dependency as an input.
                    // Flag /irc is the short form of /inputResultsCaches, and part of MSBuild 'build in isolation' mode.
                    using (argumentsBuilder.StartFragment(PipDataFragmentEscaping.NoEscaping, string.Empty))
                    {
                        argumentsBuilder.Add(PipDataAtom.FromString("/irc:"));
                        argumentsBuilder.Add(PipDataAtom.FromAbsolutePath(outputCache));
                    }
                }
            }
        }

        private void ComputeTransitiveDependenciesFor(ProjectWithPredictions project, HashSet<ProjectWithPredictions> accumulatedDependencies)
        {
            // We already computed the transitive dependencies for the required project
            if (m_transitiveDependenciesPerProject.TryGetValue(project, out var transitiveDependencies))
            {
                accumulatedDependencies.AddRange(transitiveDependencies);
                return;
            }

            foreach (ProjectWithPredictions dependency in project.ProjectReferences.Where(projectReference => projectReference.PredictedTargetsToExecute.Targets.Count != 0))
            {
                accumulatedDependencies.Add(dependency);
                ComputeTransitiveDependenciesFor(dependency, accumulatedDependencies);
            }

            m_transitiveDependenciesPerProject.TryAdd(project, accumulatedDependencies.ToReadOnlySet());
        }

        private void ProcessOutputs(ProjectWithPredictions project, ProcessBuilder processBuilder)
        {
            // Add all shared opaque directories.
            foreach (var sharedOutputDirectory in GetOutputDirectories(project))
            {
                processBuilder.AddOutputDirectory(DirectoryArtifact.CreateWithZeroPartialSealId(sharedOutputDirectory), SealDirectoryKind.SharedOpaque);
            }
        }

        /// <summary>
        /// Traverses the predicted project directory outputs and returns a collection of shared directories such that:
        /// - each directory is unique
        /// - no directory is nested within any other
        /// In addition, it adds a catch-all shared opaque directory at the root of the enlistment
        /// </summary>
        private ISet<AbsolutePath> GetOutputDirectories(
            ProjectWithPredictions project)
        {
            var sharedOutputDirectories = new HashSet<AbsolutePath>();

            // Create a catch-all shared opaque directory at the root. This will likely catch all under-predicted outputs
            // We still process the predicted outputs just in case any of those happen to fall outside the root
            sharedOutputDirectories.Add(Root);

            foreach (AbsolutePath outputDirectory in project.PredictedOutputFolders)
            {
                sharedOutputDirectories.Add(outputDirectory);
            }

            // Add user-defined additional output directories
            if (m_resolverSettings.AdditionalOutputDirectories != null)
            {
                foreach(AbsolutePath additionalOutputDirectory in m_resolverSettings.AdditionalOutputDirectories)
                {
                    sharedOutputDirectories.Add(additionalOutputDirectory);
                }
            }

            // Collapse all shared opaque directories to find common ancestors. For the most part, this should result in just the
            // catch-all shared opaque at the root
            return AbsolutePathUtilities.CollapseDirectories(sharedOutputDirectories, PathTable);
        }

        private void SetProcessEnvironmentVariables(IReadOnlyDictionary<string, string> environment, ProcessBuilder processBuilder)
        {
            foreach (KeyValuePair<string, string> kvp in environment)
            {
                if (kvp.Value != null)
                {
                    var envPipData = new PipDataBuilder(m_context.StringTable);

                    // Casing for paths is not stable as reported by BuildPrediction. So here we try to guess if the value
                    // represents a path, and normalize it
                    string value = kvp.Value;
                    if (!string.IsNullOrEmpty(value) && AbsolutePath.TryCreate(PathTable, value, out var absolutePath))
                    {
                        envPipData.Add(absolutePath);
                    }
                    else
                    {
                        envPipData.Add(value);
                    }
                    
                    processBuilder.SetEnvironmentVariable(
                        StringId.Create(m_context.StringTable, kvp.Key),
                        envPipData.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping));
                }
            }

            if (m_userDefinedPassthroughVariables != null)
            {
                foreach (string passThroughVariable in m_userDefinedPassthroughVariables)
                {
                    processBuilder.SetPassthroughEnvironmentVariable(StringId.Create(m_context.StringTable, passThroughVariable));
                }
            }
        }

        private bool TryConfigureProcessBuilder(
            ProcessBuilder processBuilder, 
            PipConstructionHelper pipConstructionHelper, 
            ProjectWithPredictions project,
            out AbsolutePath outputResultCacheFile,
            out string failureDetail)
        {
            outputResultCacheFile = AbsolutePath.Invalid;
            if (!TrySetBuildToolExecutor(pipConstructionHelper, processBuilder, project))
            {
                failureDetail = "Failed to construct tooldefinition";
                return false;
            }

            // Working directory - the directory where the project file lives.
            processBuilder.WorkingDirectory = DirectoryArtifact.CreateWithZeroPartialSealId(project.FullPath.GetParent(PathTable));

            // We allow undeclared inputs to be read
            processBuilder.Options |= Process.Options.AllowUndeclaredSourceReads;

            // Run in a container if specified
            if (m_resolverSettings.RunInContainer)
            {
                processBuilder.Options |= Process.Options.NeedsToRunInContainer;
                processBuilder.ContainerIsolationLevel = ContainerIsolationLevel.IsolateAllOutputs;
            }

            // We want to enforce the use of weak fingerprint augmentation since input predictions could be not complete/sufficient
            // to avoid a large number of path sets
            processBuilder.Options |= Process.Options.EnforceWeakFingerprintAugmentation;

            // By default the double write policy is to allow same content double writes.
            processBuilder.DoubleWritePolicy |= m_resolverSettings.DoubleWritePolicy ?? DoubleWritePolicy.AllowSameContentDoubleWrites;

            SetUntrackedFilesAndDirectories(processBuilder);

            // Add the log directory and its corresponding files
            AbsolutePath logDirectory = GetLogDirectory(project);
            processBuilder.AddOutputFile(logDirectory.Combine(PathTable, "msbuild.log"), FileExistence.Optional);
            processBuilder.AddOutputFile(logDirectory.Combine(PathTable, "msbuild.wrn"), FileExistence.Optional);
            processBuilder.AddOutputFile(logDirectory.Combine(PathTable, "msbuild.err"), FileExistence.Optional);
            processBuilder.AddOutputFile(logDirectory.Combine(PathTable, "msbuild.prf"), FileExistence.Optional);

            if (m_resolverSettings.EnableBinLogTracing == true)
            {
                processBuilder.AddOutputFile(logDirectory.Combine(PathTable, "msbuild.binlog"), FileExistence.Optional);
            }

            // Unless the legacy non-isolated mode is explicitly specified, the project builds in isolation, and therefore
            // it produces an output cache file. This file is placed on the (unique) object directory for this project
            if (m_resolverSettings.UseLegacyProjectIsolation != true)
            {
                var objectDirectory = pipConstructionHelper.GetUniqueObjectDirectory(project.FullPath.GetName(PathTable));
                outputResultCacheFile = objectDirectory.Path.Combine(PathTable, PathAtom.Create(PathTable.StringTable, OutputCacheFileName));
                processBuilder.AddOutputFile(outputResultCacheFile, FileExistence.Required);
            }

            // Path to the project
            processBuilder.ArgumentsBuilder.Add(PipDataAtom.FromAbsolutePath(project.FullPath));
            // Response file with the rest of the arguments
            var rspFileSpec = ResponseFileSpecification.Builder()
                .AllowForRemainingArguments(processBuilder.ArgumentsBuilder.CreateCursor())
                .ForceCreation(true)
                .Prefix("@")
                .Build();
                                                    
            processBuilder.SetResponseFileSpecification(rspFileSpec);

            if (!TryAddMsBuildArguments(project, processBuilder.ArgumentsBuilder, logDirectory, outputResultCacheFile, out failureDetail))
            {
                return false;
            }

            // Q_SESSION_GUID is used to provide a unique build GUID to build tools and scripts.
            // It'll cause full cache misses if we try to hash it as an input, however, so exclude.
            processBuilder.SetPassthroughEnvironmentVariable(StringId.Create(m_context.StringTable, BuildEnvironmentConstants.QSessionGuidEnvVar));

            // mspdbsrv: _MSPDBSRV_ENDPOINT_ sets up one mspdbsrv.exe instance per build target execution.
            // However this process will live beyond the build.cmd or msbuild.exe call.
            // Allow the pip job object to clean the process without complaint.
            //
            // vctip.exe: On any compile error this telemetry upload exe will be run as a detached process.
            // Just let it be killed.
            // TODO: Can we stop it running? https://stackoverflow.microsoft.com/questions/74425/how-to-disable-vctip-exe-in-vc14
            //
            // conhost.exe: This process needs a little bit more time to finish after the main process, but killing it right away
            // is inconsequential. 
            //
            // All child processes: Don't wait to kill the processes.
            // CODESYNC: CloudBuild repo TrackerExecutor.cs "info.NestedProcessTerminationTimeout = TimeSpan.Zero"
            var allowedSurvivingChildProcessNames = new List<PathAtom>() { 
                PathAtom.Create(m_context.StringTable, "mspdbsrv.exe"),
                PathAtom.Create(m_context.StringTable, "vctip.exe"),
                PathAtom.Create(m_context.StringTable, "conhost.exe")};

            // If the sandbox supports process breakaway, we configure VBCSCompiler as such
            // Otherwise, we add it as a process that is safe to kill when it survives
            var vbcsCompiler = PathAtom.Create(m_context.StringTable, "VBCSCompiler.exe");
            if (ProcessUtilities.SandboxSupportsProcessBreakaway())
            {
                processBuilder.ChildProcessesToBreakawayFromSandbox = ReadOnlyArray<PathAtom>.FromWithoutCopy(vbcsCompiler);
            }
            else
            {
                allowedSurvivingChildProcessNames.Add(vbcsCompiler);
            }
            
            processBuilder.AllowedSurvivingChildProcessNames = allowedSurvivingChildProcessNames.ToReadOnlyArray();

            // There are some cases (e.g. a 64-bit MSBuild launched as a child process from a 32-bit MSBuild instance) where
            // processes need a little bit more time to finish. Increasing the timeout does not affect job objects where no child
            // processes survive, or job object where the only surviving processes are the ones explicitly allowed to survive (which
            // are killed immediately). So overall, this non-zero timeout will only make some pips that would have failed to take a little
            // bit longer (and hopefully succeed)
            processBuilder.NestedProcessTerminationTimeout = TimeSpan.FromMilliseconds(500);

            SetProcessEnvironmentVariables(CreateEnvironment(logDirectory, project), processBuilder);

            failureDetail = string.Empty;
            return true;
        }

        private bool TryAddMsBuildArguments(ProjectWithPredictions project, PipDataBuilder pipDataBuilder, AbsolutePath logDirectory, AbsolutePath outputResultCacheFile, out string failureDetail)
        {
            // Common arguments to all MsBuildExe invocations
            pipDataBuilder.AddRange(s_commonArgumentsToMsBuildExe.Select(argument => PipDataAtom.FromString(argument)));

            // If process breakaway is supported, set up the logger that will mimic all the proper accesses
            // Otherwise, disable shared compilation
            if (ProcessUtilities.SandboxSupportsProcessBreakaway())
            {
                var vbcsCompilerLoggerPath = AbsolutePath.Create(PathTable, Assembly.GetAssembly(typeof(PipConstructor)).Location)
                    .GetParent(PathTable)
                    // Depending on the framework of the MSBuild we are using, we should provide the corresponding logger
                    // Keep in sync with BuildXL deployment
                    .Combine(PathTable, m_resolverSettings.ShouldRunDotNetCoreMSBuild()? "dotnetcore" : "net472")
                    .Combine(PathTable, VBCSCompilerLogger);

                using (pipDataBuilder.StartFragment(PipDataFragmentEscaping.NoEscaping, string.Empty))
                {
                    pipDataBuilder.Add(PipDataAtom.FromString(I($"/logger:")));
                    pipDataBuilder.Add(PipDataAtom.FromAbsolutePath(vbcsCompilerLoggerPath));
                }
            }
            else
            {
                //  Turn off new MSBuild flag to reuse VBCSCompiler.exe (a feature of Roslyn csc compiler) to compile C# files from different projects
                pipDataBuilder.Add(PipDataAtom.FromString("/p:UseSharedCompilation=false"));
            }

            // Log verbosity
            if (!TryGetLogVerbosity(m_resolverSettings.LogVerbosity, out string logVerbosity))
            {
                failureDetail = $"Cannot set the MSBuild log verbosity. '{m_resolverSettings.LogVerbosity}' is not a valid option.";
                return false;
            }

            AddLogArgument(pipDataBuilder, 1, logDirectory.Combine(PathTable, "msbuild.log"), $"Verbosity={logVerbosity}");
            AddLogArgument(pipDataBuilder, 2, logDirectory.Combine(PathTable, "msbuild.wrn"), "Verbosity=Quiet;warningsonly");
            AddLogArgument(pipDataBuilder, 3, logDirectory.Combine(PathTable, "msbuild.err"), "Verbosity=Quiet;errorsonly");
            AddLogArgument(pipDataBuilder, 4, logDirectory.Combine(PathTable, "msbuild.prf"), "PerformanceSummary");

            // Global properties on the project are turned into build parameters
            foreach(var kvp in project.GlobalProperties)
            {
                AddMsBuildProperty(pipDataBuilder, kvp.Key, kvp.Value);
            }

            // Configure binary logger if specified
            if (m_resolverSettings.EnableBinLogTracing == true)
            {
                using (pipDataBuilder.StartFragment(PipDataFragmentEscaping.NoEscaping, string.Empty))
                {
                    pipDataBuilder.Add(PipDataAtom.FromString("/binaryLogger:"));
                    pipDataBuilder.Add(PipDataAtom.FromAbsolutePath(logDirectory.Combine(PathTable, "msbuild.binlog")));
                }
            }

            // Targets to execute.
            var targets = project.PredictedTargetsToExecute.Targets;
            Contract.Assert(targets.Count > 0);
            foreach (string target in targets)
            {
                pipDataBuilder.Add(PipDataAtom.FromString($"/t:{target}"));
            }


            // Pass the output result cache file if present
            if (outputResultCacheFile != AbsolutePath.Invalid)
            {
                using (pipDataBuilder.StartFragment(PipDataFragmentEscaping.NoEscaping, string.Empty))
                {
                    // Flag /orc is the short form of /outputResultsCache, and part of MSBuild 'build in isolation' mode.
                    // By specifying this flag, MSBuild will write the build result at the end of this invocation into the cache file
                    pipDataBuilder.Add(PipDataAtom.FromString("/orc:"));
                    pipDataBuilder.Add(PipDataAtom.FromAbsolutePath(outputResultCacheFile));
                }
            }
            else
            {
                // In legacy (non-isolated) mode, we still have to rely on SDKs honoring this flag
                pipDataBuilder.Add(PipDataAtom.FromString("/p:buildprojectreferences=false")); 
            }

            failureDetail = string.Empty;
            return true;
        }

        private bool TryGetLogVerbosity(string logVerbosity, out string result)
        {
            // Keep in sync with Prelude.Configuration.Resolvers.dsc
            // logVerbosity?: "quiet" | "minimal" | "normal" | "detailed" | "diagnostic";
            // Just being defensive here. BuildXL Script typechecker should make sure the string only falls into the below cases
            switch (logVerbosity)
            {
                // Undefined means normal verbosity
                case null:
                    result = "Normal";
                    return true;
                case "quiet":
                case "minimal":
                case "normal":
                case "detailed":
                case "diagnostic":
                    // The uppercase version is what MSBuild expects.
                    result = char.ToUpperInvariant(logVerbosity[0]) + logVerbosity.Substring(1);
                    return true;
                default:
                    result = string.Empty;
                    return false;
            }
        }

        private static void AddMsBuildProperty(PipDataBuilder pipDataBuilder, string key, string value)
        {
            // Make sure properties are always quoted, since MSBuild doesn't handle semicolon separated
            // properties without quotes
            using (pipDataBuilder.StartFragment(PipDataFragmentEscaping.NoEscaping, string.Empty))
            {
                pipDataBuilder.Add(PipDataAtom.FromString("/p:"));
                using (pipDataBuilder.StartFragment(PipDataFragmentEscaping.CRuntimeArgumentRules, string.Empty))
                {
                    pipDataBuilder.Add(PipDataAtom.FromString(key));
                }
                pipDataBuilder.Add(PipDataAtom.FromString("=\""));
                using (pipDataBuilder.StartFragment(PipDataFragmentEscaping.CRuntimeArgumentRules, string.Empty))
                {
                    pipDataBuilder.Add(PipDataAtom.FromString(value));
                }
                pipDataBuilder.Add(PipDataAtom.FromString("\""));
            }
        }

        private static void AddLogArgument(PipDataBuilder pipDataBuilder, int loggerNumber, AbsolutePath logFile, string verbosity)
        {
            using (pipDataBuilder.StartFragment(PipDataFragmentEscaping.NoEscaping, string.Empty))
            {
                pipDataBuilder.Add(PipDataAtom.FromString(I($"/flp{loggerNumber}:logfile=")));
                pipDataBuilder.Add((PipDataAtom.FromAbsolutePath(logFile)));
                pipDataBuilder.Add(PipDataAtom.FromString(I($";{verbosity}")));
            }
        }

        private void SetUntrackedFilesAndDirectories(ProcessBuilder processBuilder)
        {
            // On some machines, the current user and public user desktop.ini are read by Powershell.exe.
            // Ignore accesses to the user profile and Public common user profile.
            processBuilder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(PathTable, SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.UserProfile)));

            if (Engine.TryGetBuildParameter("PUBLIC", m_frontEndName, out string publicDir))
            {             
                processBuilder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(PathTable, publicDir)));
            }

            PipConstructionUtilities.UntrackUserConfigurableArtifacts(processBuilder, m_resolverSettings);

            // Git accesses should be ignored if .git directory is there
            var gitDirectory = Root.Combine(PathTable, ".git");
            if (Engine.DirectoryExists(gitDirectory))
            {
                processBuilder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(gitDirectory));
                processBuilder.AddUntrackedFile(FileArtifact.CreateSourceFile(Root.Combine(PathTable, ".gitattributes")));
                processBuilder.AddUntrackedFile(FileArtifact.CreateSourceFile(Root.Combine(PathTable, ".gitignore")));
            }
        }

        private AbsolutePath GetLogDirectory(ProjectWithPredictions projectFile)
        {
            var success = Root.TryGetRelative(PathTable, projectFile.FullPath, out var inFolderPathFromEnlistmentRoot);
            Contract.Assert(success);

            // We hardcode the log to go under the output directory Logs/MSBuild (and follow the project structure underneath)
            // The 'official' log directory (defined by Configuration.Logging) is not stable in CloudBuild across machines, and therefore it would
            // introduce cache misses
            var result = m_frontEndHost.Configuration.Layout.OutputDirectory
                .Combine(PathTable, "Logs")
                .Combine(PathTable, "MSBuild")
                .Combine(PathTable, inFolderPathFromEnlistmentRoot);

            // Build a string with global property values (e.g. 'debug-x86'). That should be unique enough.
            // Projects can be evaluated multiple times with different global properties
            List<string> values = projectFile.GlobalProperties
                .Where(kvp => kvp.Key != s_isGraphBuildProperty)
                .Select(kvp => PipConstructionUtilities.SanitizeStringForSymbol(kvp.Value))
                .OrderBy(value => value, StringComparer.Ordinal) // Let's make sure we always produce the same string for the same set of values
                .ToList();

            if (values.Count > 0)
            {
                var valueIdentifier = string.Join("-", values);
                result = result.Combine(PathTable, valueIdentifier);
            }

            return result;
        }

        private bool TrySetBuildToolExecutor(
            PipConstructionHelper pipConstructionHelper,
            ProcessBuilder processBuilder,
            ProjectWithPredictions project)
        {
            // If we should use the dotnet core version of msbuild, the executable for the pip is dotnet.exe instead of msbuild.exe, and
            // the first argument is msbuild.dll
            FileArtifact cmdExeArtifact;
            if (m_resolverSettings.ShouldRunDotNetCoreMSBuild())
            {
                cmdExeArtifact = FileArtifact.CreateSourceFile(m_dotnetExePath);
                processBuilder.ArgumentsBuilder.Add(PipDataAtom.FromAbsolutePath(m_msBuildPath));
            }
            else {
                cmdExeArtifact = FileArtifact.CreateSourceFile(m_msBuildPath);
            }

            processBuilder.Executable = cmdExeArtifact;
            processBuilder.AddInputFile(cmdExeArtifact);
            processBuilder.AddCurrentHostOSDirectories();
            processBuilder.AddUntrackedAppDataDirectories();
            processBuilder.AddUntrackedProgramDataDirectories();

            // Temp directory setup including setting TMP and TEMP env vars. The path to
            // the temp dir is generated in a consistent fashion between BuildXL runs to
            // ensure environment value (and hence pip hash) consistency.
            processBuilder.EnableTempDirectory();

            processBuilder.ToolDescription = StringId.Create(m_context.StringTable, I($"{m_moduleDefinition.Descriptor.Name} - {project.FullPath.ToString(PathTable)}"));

            return true;
        }

        private PipConstructionHelper GetPipConstructionHelperForProject(ProjectWithPredictions project, QualifierId qualifierId)
        {
            var pathToProject = project.FullPath;

            // We might be adding the same spec file pip more than once when the same project is evaluated
            // under different global properties, but that's fine, the pip graph ignores duplicates
            m_frontEndHost.PipGraph.AddSpecFile(
                new SpecFilePip(
                    FileArtifact.CreateSourceFile(pathToProject),
                    new LocationData(pathToProject, 0, 0),
                    m_moduleDefinition.Descriptor.Id));

            Root.TryGetRelative(PathTable, pathToProject, out var specRelativePath);
            if (!PathAtom.TryCreate(m_context.StringTable, m_moduleDefinition.Descriptor.Name, out _))
            {
                throw new ArgumentException($"Failed to create PathAtom from {m_moduleDefinition.Descriptor.Name}");
            }

            // Get a symbol that is unique for this particular project instance
            var fullSymbol = GetFullSymbolFromProject(project);

            var pipConstructionHelper = PipConstructionHelper.Create(
                m_context,
                m_frontEndHost.Engine.Layout.ObjectDirectory,
                m_frontEndHost.Engine.Layout.RedirectedDirectory,
                m_frontEndHost.Engine.Layout.TempDirectory,
                m_frontEndHost.PipGraph,
                m_moduleDefinition.Descriptor.Id,
                m_moduleDefinition.Descriptor.Name,
                specRelativePath,
                fullSymbol,
                new LocationData(pathToProject, 0, 0),
                qualifierId);

            return pipConstructionHelper;
        }

        private FullSymbol GetFullSymbolFromProject(ProjectWithPredictions project)
        {
            // We construct the name of the value using the project name and its global properties
            // Observe this symbol has to be unique wrt another symbol coming from the same physical project (i.e. same project full
            // path) but different global properties. The project full path is already being passed as part of the 'key' when creating the 
            // pip construction helper
            var valueName = PipConstructionUtilities.SanitizeStringForSymbol(project.FullPath.GetName(PathTable).ToString(m_context.StringTable));

            // If global properties are present, we append to the value name a flatten representation of them
            // There should always be a 'IsGraphBuild' property, so we count > 1
            Contract.Assert(project.GlobalProperties.ContainsKey(s_isGraphBuildProperty));
            if (project.GlobalProperties.Count > 1)
            {
                valueName += ".";
            }
            valueName += string.Join(
                ".",
                project.GlobalProperties
                    // let's sort global properties keys to make sure the same string is generated consistently
                    // case-sensitivity is already handled (and ignored) by GlobalProperties class 
                    .Where(kvp => kvp.Key != s_isGraphBuildProperty)
                    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal) 
                    .Select(gp => $"{PipConstructionUtilities.SanitizeStringForSymbol(gp.Key)}_{PipConstructionUtilities.SanitizeStringForSymbol(gp.Value)}"));

            var fullSymbol = FullSymbol.Create(m_context.SymbolTable, valueName);
            return fullSymbol;
        }

        /// <summary>
        /// Computes a sha256 based on the project properties
        /// </summary>
        private string ComputeSha256(ProjectWithPredictions projectWithPredictions)
        {
            using (var builderWrapper = Pools.GetStringBuilder())
            {
                // full path, global properties and targets to execute should uniquely identify the project within the build graph
                StringBuilder builder = builderWrapper.Instance;
                builder.Append(projectWithPredictions.FullPath.ToString(PathTable));
                builder.Append("|");
                builder.Append(projectWithPredictions.PredictedTargetsToExecute.IsDefaultTargetsAppended);
                builder.Append(string.Join("|", projectWithPredictions.PredictedTargetsToExecute.Targets));
 
                builder.Append("|");
                builder.Append(string.Join("|", projectWithPredictions.GlobalProperties.Select(kvp => kvp.Key + "|" + kvp.Value)));

                return PipConstructionUtilities.ComputeSha256(builder.ToString());
            }
        }

        /// <summary>
        /// Represents the written outputs (that matter for scheduling inputs) of an MSBuild project
        /// </summary>
        /// <remarks>
        /// This is just a handy struct to store across scheduled pips, and therefore private to the pip constructor
        /// </remarks>
        private readonly struct MSBuildProjectOutputs
        {
            /// <summary>
            /// Creates a project that is built in isolation, and therefore it produces an output cache file 
            /// </summary>
            public static MSBuildProjectOutputs CreateIsolated(IEnumerable<StaticDirectory> outputDirectories, FileArtifact outputCacheFile)
            {
                Contract.Requires(outputDirectories != null);
                Contract.Requires(outputCacheFile != FileArtifact.Invalid);

                return new MSBuildProjectOutputs(outputDirectories, outputCacheFile);
            }

            /// <summary>
            /// Creates a project that is built using the legacy mode (non-isolated)
            /// </summary>
            public static MSBuildProjectOutputs CreateLegacy(IEnumerable<StaticDirectory> outputDirectories)
            {
                Contract.Requires(outputDirectories != null);
                return new MSBuildProjectOutputs(outputDirectories, FileArtifact.Invalid);
            }

            private MSBuildProjectOutputs(IEnumerable<StaticDirectory> outputDirectories, FileArtifact outputCacheFile)
            {
                OutputDirectories = outputDirectories;
                OutputCacheFile = outputCacheFile;
            }

            /// <summary>
            /// Whether this project should be built in isolation
            /// </summary>
            public bool BuildsInIsolation => OutputCacheFile != FileArtifact.Invalid;

            /// <summary>
            /// All output directories this project writes into
            /// </summary>
            public IEnumerable<StaticDirectory> OutputDirectories { get; }

            /// <summary>
            /// The output cache file that gets generated when this project builds in isolation
            /// </summary>
            /// <remarks>
            /// Invalid when the legacy (non-isolated) mode is used
            /// </remarks>
            public FileArtifact OutputCacheFile { get; } 
        }
    }
}
