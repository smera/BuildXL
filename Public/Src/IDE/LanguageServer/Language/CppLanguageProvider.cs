// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Configuration;
// using System.Runtime.Remoting.Messaging;
using BuildXL.FrontEnd.Script.Declarations;
using BuildXL.Ide.JsonRpc;
using BuildXL.Ide.LanguageServer.Providers;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using LanguageServer;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace BuildXL.Ide.LanguageServer.Languages
{
    /// <summary>
    /// Handles request from vscode for dscript-cpp language extension
    /// </summary>
    public class CppLanguageProvider
    {
        private readonly GetAppState m_getAppData;
        /// <nodoc/>
        public CppLanguageProvider(GetAppState getAppData)
        {
            m_getAppData = getAppData;
        }

        /// <summary>
        /// Returns the module descriptors for files present in the BuildXL workspace
        /// </summary>
        /// <remarks>
        /// This extends the language server protocol.
        /// </remarks>
        [JsonRpcMethod("dscript-cpp/canProvideConfiguration")]
        protected Result<CanProvideConfigurationResult, ResponseError> CanProvideConfiguration(JToken token)
        {
            var canProvideConfigurationParams = token.ToObject<CanProvideConfigurationParams>();

            var findResult = TryFindPips(new[] { canProvideConfigurationParams.Uri }, out var pathTable);
            if (findResult.IsError)
            {
                return Result<CanProvideConfigurationResult, ResponseError>.Error(findResult.ErrorValue);
            }

            return Result<CanProvideConfigurationResult, ResponseError>.Success(
                new CanProvideConfigurationResult
                {
                    CanProvideConfiguration = true, // TODO: findResult.SuccessValue[0] != null,
                }
            );
        }

    /// <summary>
    /// Returns the module descriptors for files present in the BuildXL workspace
    /// </summary>
    /// <remarks>
    /// This extends the language server protocol.
    /// </remarks>
    [JsonRpcMethod("dscript-cpp/provideConfiguration")]
    protected Result<ProvideConfigurationsResult, ResponseError> ProvideConfiguration(JToken token)
    {
        var provideConfigurationsParams = token.ToObject<ProvideConfigurationsParams>();

        var findResult = TryFindPips(provideConfigurationsParams.Uris, out var pathTable);
        if (findResult.IsError)
        {
            return Result<ProvideConfigurationsResult, ResponseError>.Error(findResult.ErrorValue);
        }

        var processes = findResult.SuccessValue;

        // TODO: Dedupe on process
        var sourceFileConfigurations = new SourceFileConfigurationItem[processes.Length];
        for (int i = 0; i < processes.Length; i++)
        {
            var extractResult = ExtractFileConfigFromProcess(pathTable, provideConfigurationsParams.Uris[i], processes[i]);
            if (extractResult.IsError)
            {
                return Result<ProvideConfigurationsResult, ResponseError>.Error(
                    new ResponseError
                    {
                        code = ErrorCodes.InternalError,
                        message = BuildXL.Ide.LanguageServer.Strings.WorkspaceParsingFailedCannotPerformAction,
                    });
            }

            sourceFileConfigurations[i] = extractResult.SuccessValue;
        }

        return Result<ProvideConfigurationsResult, ResponseError>.Success(
            new ProvideConfigurationsResult
            {
                SourceFileConfigurations = sourceFileConfigurations,
            });
    }

    private Result<Process[], ResponseError> TryFindPips(string[] uris, out PathTable pathTable)
    {
        var paths = new AbsolutePath[uris.Length];
        var result = new Process[uris.Length];
        var resultsToCollect = uris.Length;

        var appState = m_getAppData();

        for (int i = 0; i < uris.Length; i++)
        {
            if (!AbsolutePath.TryCreate(appState.PathTable, uris[i], out var path))
            {
                pathTable = null;
                return Result<Process[], ResponseError>.Error(
                    new ResponseError
                    {
                        code = ErrorCodes.InternalError,
                        message = string.Format(BuildXL.Ide.LanguageServer.Strings.NotAValidPath, uris[i]),
                    });
            }
            paths[i] = path;
        }

        if (appState == null)
        {
            pathTable = null;
            return Result<Process[], ResponseError>.Error(new ResponseError
            {
                code = ErrorCodes.InternalError,
                message = BuildXL.Ide.LanguageServer.Strings.WorkspaceParsingFailedCannotPerformAction,
            });
        }

        pathTable = appState.PathTable;

        // $TODO: Consider if we can provide a hint for the evaluation filter since likely a spec filter with the parent folder of the cpp file should result in a hit.
        var languageModel = appState.IncrementalLanguageModelProvider.WaitForRecomputationToFinish();
        var pipGraph = languageModel.PipGraph;

        var clTool = PathAtom.Create(pathTable.StringTable, "cl.exe");

        // $TODO: This does not scale, we need to have a PipGraph with better navigation Api's. The implementation class has it, but IPipGraph does not :(
        var allPips = pipGraph.RetrieveScheduledPips();
        foreach (var pip in allPips)
        {
            if (pip is Process process)
            {
                var processExe = process.Executable.Path.GetName(pathTable);
                if (processExe == clTool)
                {
                    // cl candidate
                    // TODO: This only checks for direct dependencies and ignores sealed directories for now.
                    foreach (var dep in process.Dependencies)
                    {
                        for (int i = 0; i < paths.Length; i++)
                        {
                            if (result[i] == null && dep.Path == paths[i])
                            {
                                result[i] = process;
                                resultsToCollect--;
                            }
                        }

                        if (resultsToCollect == 0)
                        {
                            // Stop the enumeration when all files have been found.
                            break;
                        }
                    }
                }
            }
        }

        // not found
        return Result<Process[], ResponseError>.Success(result);
    }

    // TODO: Handle uri vs fspath...

    private Result<SourceFileConfigurationItem, ResponseError> ExtractFileConfigFromProcess(PathTable pathTable, string uri, Process process)
    {
        const string includesKey = "ide.includes:";
        const string definesKey = "ide.defines:";

        var configuration = new SourceFileConfiguration();


        foreach (var tagId in process.Tags)
        {
            var tag = tagId.ToString(pathTable.StringTable);
            if (tag.StartsWith(includesKey, System.StringComparison.Ordinal))
            {
                configuration.IncludePath = tag.Substring(includesKey.Length).Split(';');
            }
            else if (tag.StartsWith(definesKey, System.StringComparison.Ordinal))
            {
                configuration.Defines = tag.Substring(includesKey.Length).Split(';');
            }
        }

        configuration.IntelliSenseMode = "msvc-x64";
        configuration.Standard = "c++17";

        var result = new SourceFileConfigurationItem
        {
            Uri = uri,
            Configuration = configuration,
            
        };

        return Result<SourceFileConfigurationItem, ResponseError>.Success(result);
    }
}
}
