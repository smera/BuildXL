// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BuildXL.Ide.JsonRpc;
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
        private readonly GetAppState m_getAppState;

        /// <nodoc/>
        public CppLanguageProvider(GetAppState getAppState)
        {
            m_getAppState = getAppState;
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

            var result = new CanProvideConfigurationResult
            {
                CanProvideConfiguration = false,
            };

            return Result<CanProvideConfigurationResult, ResponseError>.Success(result);
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

            var result = new ProvideConfigurationsResult
            {
                SourceFileConfigurations = new SourceFileConfigurationItem[0],
            };

            return Result<ProvideConfigurationsResult, ResponseError>.Success(result);
        }
    }
}
