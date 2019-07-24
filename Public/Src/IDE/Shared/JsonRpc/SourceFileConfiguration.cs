// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;

namespace BuildXL.Ide.JsonRpc
{
    /// <summary>
    /// The model representing the custom IntelliSense configurations for a source file.
    /// </summary>
    [DataContract]
    public sealed class SourceFileConfiguration
    {
        /// <summary>
        /// This must also include the system include path (compiler defaults) unless
        /// [compilerPath](#SourceFileConfiguration.compilerPath) is specified.
        /// </summary>
        [DataMember(Name = "includePath")]
        public string[] IncludePath { get; set; }

        /// <summary>
        /// This must also include the compiler default defines (__cplusplus, etc) unless
        /// [compilerPath](#SourceFileConfiguration.compilerPath) is specified.
        /// </summary>
        [DataMember(Name = "defines")]
        public string[] Defines { get; set; }

        /// <summary>
        /// The compiler to emulate.
        /// "msvc-x64" | "gcc-x64" | "clang-x64"
        /// </summary>
        [DataMember(Name = "intelliSenseMode")]
        public string IntelliSenseMode { get; set; }

        /// <summary>
        /// The C or C++ standard to emulate.
        /// "c89" | "c99" | "c11" | "c++98" | "c++03" | "c++11" | "c++14" | "c++17";
        /// </summary>
        [DataMember(Name = "standard")]
        public string Standard { get; set; }

        /// <summary>
        /// Any files that need to be included before the source file is parsed.
        /// </summary>
        [DataMember(Name = "forcedInclude", EmitDefaultValue = false)]
        public string[] ForcedInclude { get; set; }

        /// <summary>
        /// The full path to the compiler. If specified, the extension will query it for system includes and defines and
        /// add them to [includePath](#SourceFileConfiguration.includePath) and [defines](#SourceFileConfiguration.defines).
        /// </summary>
        [DataMember(Name = "compilerPath", EmitDefaultValue = false)]
        public string CompilerPath { get; set; }

        /// <summary>
        /// The version of the Windows SDK that should be used. This field will only be used if
        /// [compilerPath](#SourceFileConfiguration.compilerPath) is set and the compiler is capable of targeting Windows.
        /// </summary>
        [DataMember(Name = "windowsSdkVersion", EmitDefaultValue = false)]
        public string WindowsSdkVersion { get; set; }
    }
}