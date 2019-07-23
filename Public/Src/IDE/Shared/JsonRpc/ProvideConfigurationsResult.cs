// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;

namespace BuildXL.Ide.JsonRpc
{
    /// <summary>
    /// Represents the result of the "dscript-cpp/provideConfigurations" JSON RPC request.
    /// </summary>
    [DataContract]
    public sealed class ProvideConfigurationsResult
    {
        /// <summary>
        /// A list of [SourceFileConfigurationItem](#SourceFileConfigurationItem) for the documents that this provider
        /// is able to provide IntelliSense configurations for.
        /// Note: If this provider cannot provide configurations for any of the files in `uris`, the provider may omit the
        /// configuration for that file in the return value. An empty array may be returned if the provider cannot provide
        /// configurations for any of the files requested.
        /// </summary>
        [DataMember(Name = "sourceFileConfigurations")]
        public SourceFileConfigurationItem[] SourceFileConfigurations { get; set; }
    };
}