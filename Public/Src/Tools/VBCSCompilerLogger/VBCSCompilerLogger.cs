// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Native.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace VBCSCompilerLogger
{
    /// <summary>
    /// This logger catches csc and vbc MSBuild tasks and uses the command line argument passed to the compiler to mimic the file accesses that the compiler 
    /// would have produced.
    /// </summary>
    public class CompilerFileAccessLogger : Logger
    {
        private const string CscTaskName = "Csc";
        private const string VbcTaskName = "Vbc";
        private const string CscToolName = "csc.exe";
        private const string VbcToolName = "vbc.exe";

        private HashSet<string> m_inputs = new HashSet<string>();
        private HashSet<string> m_outputs = new HashSet<string>();
        private HashSet<string> m_enumerations = new HashSet<string>();

        /// <inheritdoc/>
        public override void Initialize(IEventSource eventSource)
        {
            eventSource.MessageRaised += EventSourceOnMessageRaised;
            eventSource.BuildFinished += EventSourceOnBuildFinished;
        }

        private void EventSourceOnBuildFinished(object sender, BuildFinishedEventArgs e)
        {
            // Let's mimic all inputs and outputs now the build (i.e. the msbuild invocation for this particular project)
            // is done. Even if VBCSCompiler is around, all output files should have been already created since we are getting the
            // build finished event
            lock (m_inputs)
            {
                foreach (string input in m_inputs)
                {
                    // We want to mimic a file open. If the file does not exist, do nothing, but the attempt will be
                    // observed, which is what we want so an anti-dependency is registered
                    try
                    {
                        using (new FileStream(input, FileMode.Open, FileAccess.Read)) { }
                    }
                    catch (FileNotFoundException) { }
                }
            }

            lock (m_enumerations)
            {
                foreach(string directory in m_enumerations)
                {
                    FileUtilities.EnumerateDirectoryEntries(directory, recursive: false, (s1, s2, attr) => { });
                }
            }

            lock (m_outputs)
            {
                foreach (string output in m_outputs)
                {
                    // Al predicted outputs should be there, but if some are missing, it is not really problematic,
                    // so there is no point in letting the pip fail because of it
                    try
                    {
                        using (new FileStream(output, FileMode.Open, FileAccess.Write)) { }
                    }
                    catch (FileNotFoundException) { }
                }
            }
        }

        private void EventSourceOnMessageRaised(object sender, BuildMessageEventArgs e)
        {
            if (e is TaskCommandLineEventArgs commandLine)
            {
                // We are only interested in CSharp and VisualBasic tasks
                string language;
                string arguments;
                switch (commandLine.TaskName)
                {
                    case CscTaskName:
                        language = LanguageNames.CSharp;
                        arguments = GetArgumentsFromCommandLine(CscToolName, commandLine.CommandLine);
                        break;
                    case VbcTaskName:
                        language = LanguageNames.VisualBasic;
                        arguments = GetArgumentsFromCommandLine(VbcToolName, commandLine.CommandLine);
                        break;
                    default:
                        return;
                }

                // We were able to split the compiler invocation from its arguments. This is the indicator
                // that something didn't go as expected. Since failing to parse the command line means we
                // are not catching all inputs/outputs properly, we have no option but to fail the corresponding pip
                if (arguments == null)
                {
                    throw new ArgumentException($"Unexpected tool name in command line. Expected '{CscToolName}' or '{VbcToolName}', but got: {commandLine.CommandLine}");
                }

                var parsedCommandLine = CompilerUtilities.GetParsedCommandLineArguments(language, arguments, commandLine.ProjectFile);
                RegisterAccesses(parsedCommandLine);
            }
        }

        private string GetArgumentsFromCommandLine(string toolToTrim, string commandLine)
        {
            toolToTrim += " ";
            int index = commandLine.IndexOf(toolToTrim, StringComparison.OrdinalIgnoreCase);
            
            if (index == -1)
            {
                return null;
            }

            return commandLine.Substring(index + toolToTrim.Length);
        }

        private void RegisterAccesses(CommandLineArguments args)
        {
            //System.Diagnostics.Debugger.Launch();
            // All inputs
            RegisterInputs(args.SourceFiles.Select(source => source.Path));
            RegisterInputs(args.AnalyzerReferences.Select(reference => reference.FilePath));
            RegisterInputs(args.EmbeddedFiles.Select(embedded => embedded.Path));
            RegisterInput(args.Win32ResourceFile);
            RegisterInput(args.Win32Icon);
            RegisterInput(args.Win32Manifest);
            RegisterInputs(args.AdditionalFiles.Select(additional => additional.Path));
            RegisterInputs(args.MetadataReferences.Select(metadata => metadata.Reference).Where(pathOrAssemblyName => FileUtilities.FileSystem.IsPathRooted(pathOrAssemblyName)));
            RegisterInputEnumerations(args.ReferencePaths);
            RegisterInputEnumerations(args.SourcePaths);
            //RegisterInputEnumerations(args.KeyFileSearchPaths);
            RegisterInput(args.AppConfigPath);
            RegisterInput(args.RuleSetPath);
            RegisterInput(args.SourceLink);
            RegisterInputs(args.AnalyzerReferences.Select(analyzerRef => analyzerRef.FilePath));

            // All outputs
            RegisterOutput(args.DocumentationPath);
            RegisterOutput(args.ErrorLogPath);
            RegisterOutput(args.OutputRefFilePath);

            // TODO: revise this logic, it is more complicated than this when the output file is not specified
            string outputFileName = args.OutputFileName ?? (Path.GetFileNameWithoutExtension(args.SourceFiles[0].Path) + ".dll");
            
            RegisterOutput(Path.Combine(args.OutputDirectory, outputFileName));
            if (args.EmitPdb)
            {
                RegisterOutput(Path.Combine(args.OutputDirectory, args.PdbPath ?? Path.ChangeExtension(outputFileName, ".pdb")));
            }
        }

        private void RegisterOutput(string filePath)
        {
            lock (m_outputs)
            {
                RegisterAccess(m_outputs, filePath);
            }
        }

        private void RegisterInputEnumerations(IEnumerable<string> directories)
        {
            lock (m_enumerations)
            {
                foreach(string directory in directories)
                {
                    if (!string.IsNullOrEmpty(directory))
                    {
                        m_enumerations.Add(directory);
                    }
                }
            }
        }

        private void RegisterInput(string filePath)
        {
            lock (m_inputs)
            {
                RegisterAccess(m_inputs, filePath);
            }
        }

        private void RegisterInputs(IEnumerable<string> filePaths)
        {
            lock (m_inputs)
            {
                RegisterAccesses(m_inputs, filePaths);
            }
        }

        private void RegisterAccesses(HashSet<string> accessCollection, IEnumerable<string> filePaths)
        {
            foreach (string filePath in filePaths)
            {
                RegisterAccess(accessCollection, filePath);
            }
        }

        private void RegisterAccess(HashSet<string> accessCollection, string filePath)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                lock (accessCollection)
                {
                    accessCollection.Add(filePath);
                }
            }
        }
    }
}