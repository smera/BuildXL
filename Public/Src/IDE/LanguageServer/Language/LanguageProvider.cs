// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using BuildXL.Utilities;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.Ide.JsonRpc;
using JetBrains.Annotations;
using LanguageServer;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

using BuildXLModuleDescriptor = BuildXL.FrontEnd.Workspaces.Core.ModuleDescriptor;

namespace BuildXL.Ide.LanguageServer.Languages
{
    /// <summary>
    /// Managues the language specific request from vscode for dscript.
    /// </summary>
    public class LanguageProvider
    {
        private readonly CppLanguageProvider m_cpp;

        /// <nodoc />
        public LanguageProvider(GetAppState getAppState, StreamJsonRpc.JsonRpc rpcChannel)
        {
            m_cpp = new CppLanguageProvider(getAppState);
            rpcChannel.AddLocalRpcTarget(m_cpp);
        }
    }
}
