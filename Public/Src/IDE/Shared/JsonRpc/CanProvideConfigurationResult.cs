// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;

namespace BuildXL.Ide.JsonRpc
{
    /// <summary>
    /// Represents the result of the "dscript-cpp/canProvideConfiguration" JSON RPC request.
    /// </summary>
    [DataContract]
    public sealed class CanProvideConfigurationResult
    {
        /// <summary>
        /// 'true' if this provider can provide IntelliSense configurations for the given document.
        /// </summary>
        [DataMember(Name = "canProvideConfiguration")]
        public bool CanProvideConfiguration { get; set; }
    };
}
