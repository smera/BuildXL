// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;

namespace BuildXL.Ide.JsonRpc
{
    /// <summary>
    /// Represents the result of the "dscript-cpp/provideConfigurations" JSON RPC request.
    /// A request to get Intellisense configurations for the given files.
    /// </summary>
    [DataContract]
    public sealed class ProvideConfigurationsParams
    {
        /// <summary>
        /// A list of one of more URIs for the files to provide configurations for.
        /// </summary>
        [DataMember(Name = "uris")]
        public string[] Uris { get; set; }
    };
}
