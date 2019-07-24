// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BuildXL.Utilities;
using BuildXL.FrontEnd.Workspaces;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.FrontEnd.Script.Analyzer;
using BuildXL.FrontEnd.Core;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using TypeScript.Net.Types;
using BuildXL.Pips;
using BuildXL.FrontEnd.Sdk;
using BuildXL.Engine;

namespace BuildXL.Ide.LanguageServer.Providers
{
    /// <summary>
    /// Incrementally recomputes the language service semantic model based on individual document change events
    /// </summary>
    public sealed class IncrementalLanguageModelProvider : IDisposable
    {
        // Time that are waited from the last document change event and before a workspace recomputation occurs
        private static readonly TimeSpan RecompilationTimeout = TimeSpan.FromMilliseconds(125);
        private readonly FrontEndContext m_frontEndContext;
        private readonly EngineContext m_engineContext;
        private readonly FrontEndEngineAbstraction m_engineAbstraction;
        private readonly EventHandler<WorkspaceProgressEventArgs> m_progressHandler;
        private readonly bool m_skipNuget;
        private readonly AbsolutePath m_rootFolder;
        private readonly FrontEndHostController m_controller;
        private Workspace m_workspace;
        private readonly TextDocumentManager m_documentManager;
        private readonly TestContext? m_testContext;
        private readonly LanguageModel m_languageModel;
        private readonly PathTable m_pathTable;

        private readonly object m_recomputationLock = new object();

        private readonly Timer m_timer;

        // Stack of changes that are coalesced until the workspace recomputation is triggered
        private ConcurrentStack<TextDocumentItem> m_stackChangeEvents = new ConcurrentStack<TextDocumentItem>();

        private readonly ManualResetEvent m_recomputationInProgress = new ManualResetEvent(true);

        private bool m_disposed = false;

        private bool SchedulePips => m_languageModel.PipGraph != null;

        /// <summary>
        /// The given workspace represents the original workspace. A callback is invoked every time this provider decides to recompute the workspace.
        /// </summary>
        public IncrementalLanguageModelProvider(
            FrontEndContext frontEndContext,
            EngineContext engineContext,
            FrontEndEngineAbstraction engineAbstraction,
            EventHandler<WorkspaceProgressEventArgs> progressHandler,
            bool skipNuget,
            AbsolutePath rootFolder,
            FrontEndHostController controller, 
            Workspace workspace, 
            TextDocumentManager documentManager, 
            IPipGraph pipGraph, 
            TestContext? testContext)
        {
            m_frontEndContext = frontEndContext;
            m_engineContext = engineContext;
            m_engineAbstraction = engineAbstraction;
            m_progressHandler = progressHandler;
            m_skipNuget = skipNuget;
            m_rootFolder = rootFolder;
            m_controller = controller;
            m_documentManager = documentManager;
            m_testContext = testContext;
            m_languageModel = new LanguageModel(workspace, pipGraph);
            m_workspace = m_languageModel.Workspace;
            documentManager.Changed += DocumentChanged;
            m_pathTable = m_engineContext.PathTable;
            m_timer = new Timer(_ => RecomputeWorkspaceForLatestChangedDocuments(), state: null, dueTime: Timeout.Infinite, period: Timeout.Infinite);
        }

        /// <summary>
        /// Threads that want to make sure there are not in-flight text changes that don't have an associated workspace yet should call
        /// this to wait until all text changes have been applied
        /// </summary>
        public LanguageModel WaitForRecomputationToFinish()
        {
            ThrowObjectDisposedExceptionIfNeeded();

            m_recomputationInProgress.WaitOne();
            return m_languageModel;
        }

        /// <summary>
        /// Event handler that gets fired on workspace recomputation.
        /// </summary>
        public event EventHandler<WorkspaceRecomputedEventArgs> WorkspaceRecomputed;

        private void ThrowObjectDisposedExceptionIfNeeded()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException("The instance of IncrementalLanguageModelProvider is already disposed.");
            }
        }

        /// <summary>
        /// A change in a document occurred
        /// </summary>
        private void DocumentChanged(object sender, TextDocumentChangedEventArgs e)
        {
            // The instance may be disposed already.
            if (m_disposed)
            {
                return;
            }

            lock (m_recomputationLock)
            {
                // Threads that wait on this to be completed will block now
                m_recomputationInProgress.Reset();
                m_stackChangeEvents.Push(e.Document);
            }

            if (m_testContext?.ForceNoRecomputationDelay == true)
            {
                RecomputeWorkspaceForLatestChangedDocuments();
            }
            else
            {
                // Reset the recomputation timer to fire in RecompilationTimeout time
                // Observe that this cannot be in the critical section above because it can result
                // in deadlock with RecomputeWorkspaceForLatestChangeEvent thread
                // (when that thread fires, it acquires lock in m_stoppableTimer, and Change function also requires same lock.
                // If this call was in CS, both would be reliant on m_recomputationLock, risking deadlock).
                m_timer.Change((int)RecompilationTimeout.TotalMilliseconds, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Re-computes the workspace based on the latest changes to each changed document.
        /// (In the case of rename, multiple documents may change at the same time)
        /// </summary>
        private void RecomputeWorkspaceForLatestChangedDocuments()
        {
            // Capture the current state of the stack and empty it by re-initializing it
            var stackSnapshot = Interlocked.Exchange(ref m_stackChangeEvents, new ConcurrentStack<TextDocumentItem>());

            if (stackSnapshot.IsEmpty)
            {
                // Somebody else concurrently emptied the stack, this should never have happened
                throw new InvalidOperationException("StoppableTimer should never call 2 callbacks at the same time. So this should never have happened");
            }

            // Need to recompute the workspace based on the latest changes for each file that has been changed.
            // TODO: This may have performance issues if there are a lot of files that needs to be changed.
            // TODO: Need to optimize RecomputeWorkspace to handle multiple file changes efficiently.
            // TODO: Task 15106291: Optimize RecomputeWorkspace to handle multiple files.
            var changedDocuments = new HashSet<string>();

            foreach (var doc in stackSnapshot)
            {
                if (changedDocuments.Add(doc.Uri))
                {
                    RecomputeWorkspace(doc);
                }
            }

            lock (m_recomputationLock)
            {
                if (!m_stackChangeEvents.IsEmpty)
                {
                    // By the time we finished recomputing the workspace, it is possible that new changes were made
                    // In that case the workspace we just recomputed is outdated, so any threads waiting for updated workspace
                    // should stay waiting until the next recomputation
                    return;
                }

                // Wake up any waiting threads
                m_recomputationInProgress.Set();
            }

            // Let listeners know the workspace was recomputed
            WorkspaceRecomputed?.Invoke(this, new WorkspaceRecomputedEventArgs(m_workspace, stackSnapshot.ToArray()));
        }

        private void RecomputeWorkspace(TextDocumentItem document)
        {
            var path = document.Uri.ToAbsolutePath(m_pathTable);

            // Need to check if parsedModule is prelude or configuration module.
            // If the parsed module is the configuration module, then full reanalysis is required.
            ParsedModule parsedModule = m_workspace.TryGetModuleBySpecFileName(path);
            ModuleDefinition moduleDefinition;
            if (parsedModule != null)
            {
                moduleDefinition = parsedModule.Definition;
            }
            else
            {
                // This should never happen: all the new files trigger full workspace reconstruction.
                return;
            }

            try
            {
                var updatedSemanticModel = GetUpdatedLanguageModel(parsedModule, moduleDefinition, path);
                m_workspace = updatedSemanticModel.Workspace;
            }
            catch (Exception e)
            {
                // TODO: saqadri - log exception, but for now just swallow
                Debug.Fail($"Workspace recomputation threw an exception: {e}");
            }
        }

        private LanguageModel GetUpdatedLanguageModel(ParsedModule parsedModule, ModuleDefinition moduleDefinition, AbsolutePath path)
        {
            // Need to use both 'ParsedModule' and 'ModuleDefinition' in case if the 'path' is newly added
            // file and was not parsed yet.

            // All already parsed modules are good to go
            var unchangedModules = m_workspace.SpecModules;

            // We build a module under construction that has all specs but the one that changed
            var moduleUnderConstruction = new ModuleUnderConstruction(moduleDefinition);
            foreach (var spec in parsedModule.Specs)
            {
                if (spec.Key != path)
                {
                    moduleUnderConstruction.AddParsedSpec(spec.Key, spec.Value);
                }
            }

            // Update parsing failures so the ones coming from the changed spec are removed
            var filteredFailures = m_workspace.Failures.Where(
                failure => (failure is ParsingFailure parsingFailure && parsingFailure.SourceFile.Path.AbsolutePath != path.ToString(m_pathTable)));

            var updatedWorkspace =
                m_workspace.WorkspaceProvider.CreateIncrementalWorkspaceForAllKnownModulesAsync(
                    unchangedModules,
                    moduleUnderConstruction,
                    filteredFailures,
                    m_workspace.PreludeModule)
                    .GetAwaiter()
                    .GetResult();

            var semanticWorkspaceProvider = new SemanticWorkspaceProvider(new WorkspaceStatistics(), m_workspace.WorkspaceConfiguration);
            var semanticWorkspace =
                semanticWorkspaceProvider.ComputeSemanticWorkspaceAsync(m_pathTable, updatedWorkspace, m_workspace.GetSemanticModel(), incrementalMode: true)
                    .GetAwaiter()
                    .GetResult();

            // Get all the potentially new specs. Old ones, besides the one that changed, were aready linted
            var newPathToSpecs =
                updatedWorkspace.GetAllSpecFiles()
                    .Except(m_workspace.GetAllSpecFiles())
                    .Concat(new[] { path });

            // The changed spec always needs linting. Plus all the new ones the changed spec may have introduced.
            var preludeModule = updatedWorkspace.PreludeModule;
            var specsToLint = new HashSet<ISourceFile>();

            foreach (var pathToSpec in newPathToSpecs)
            {
                // Need to exclude prelude files from liinting.
                // Need to keep config files. They should be linted.
                if (preludeModule == null || !preludeModule.Specs.ContainsKey(pathToSpec))
                {
                    specsToLint.Add(updatedWorkspace.GetSourceFile(pathToSpec));
                }
            }

            // If we are not scheduling pips, let's make sure the workspace is fully linted
            // (if we are, we will be linting anyway)
            if (!SchedulePips)
            {
                var lintedWorkspace = WorkspaceBuilder.CreateLintedWorkspaceForChangedSpecs(
                semanticWorkspace,
                specsToLint,
                m_controller.FrontEndContext.LoggingContext,
                m_controller.FrontEndConfiguration,
                m_controller.FrontEndContext.PathTable);

                return new LanguageModel(lintedWorkspace, pipGraph: null);
            }

            if (!WorkspaceBuilder.TrySchedulePipsForExistingWorkspace(m_frontEndContext, m_engineContext, m_engineAbstraction, m_progressHandler, m_rootFolder, semanticWorkspace, m_skipNuget, out var pipGraph))
            {
                // TODO: error handling missing. For now, just don't swallow the error.
                throw new Exception("Cannot schedule pips for workspace");
            }

            return new LanguageModel(semanticWorkspace, pipGraph);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!m_disposed)
            {
                m_disposed = true;

                m_documentManager.Changed -= DocumentChanged;

                m_timer.Dispose();

                m_recomputationInProgress.Dispose();
            }
        }
    }
}
