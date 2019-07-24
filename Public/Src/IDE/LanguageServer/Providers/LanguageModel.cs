// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.ContractsLight;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.Pips;

namespace BuildXL.Ide.LanguageServer.Providers
{
    /// <summary>
    /// The language semantic information computed for a given repository
    /// </summary>
    public struct LanguageModel
    {
        /// <nodoc/>
        public LanguageModel(Workspace workspace, IPipGraph pipGraph)
        {
            Contract.Requires(workspace != null);

            Workspace = workspace;
            PipGraph = pipGraph;
        }

        /// <nodoc/>
        public Workspace Workspace { get; }

        /// <summary>
        /// Can be null if the plugin is only doing Dscript intellisense
        /// </summary>
        public IPipGraph PipGraph { get; }
    }
}
