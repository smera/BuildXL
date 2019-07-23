// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;

namespace BuildXL.Ide.JsonRpc
{
    /// <summary>
    /// Represents the result of the "dscript-cpp/canProvideConfiguration" JSON RPC request.
    /// </summary>
    [DataContract]
    public sealed class CanProvideConfigurationParams
    {
        /// <summary>
        /// The URI of the document.
        /// </summary>
        [DataMember(Name = "uri")]
        public string Uri { get; set; }
    };
}
