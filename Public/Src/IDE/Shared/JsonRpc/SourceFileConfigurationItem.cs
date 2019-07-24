// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;

namespace BuildXL.Ide.JsonRpc
{
    /// <summary>
    /// The model representing a source file and its corresponding configuration.
    /// </summary>
    [DataContract]
    public sealed class SourceFileConfigurationItem
    {
        /// <summary>
        /// The URI of the source file. It should follow the file URI scheme and represent an absolute path to the file.
        /// </summary>
        [DataMember(Name = "uri")]
        public string Uri { get; set; }

        /// <summary>
        /// The IntelliSense configuration for [uri](#SourceFileConfigurationItem.uri)
        /// </summary>
        [DataMember(Name = "configuration")]
        public SourceFileConfiguration Configuration { get; set; }
    }
}