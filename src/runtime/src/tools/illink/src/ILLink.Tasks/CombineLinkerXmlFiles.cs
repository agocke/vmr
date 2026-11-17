// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ILLink.Tasks
{
    /// <summary>
    /// Combines multiple ILLink xml files into a single xml file.
    /// </summary>
    [MSBuildDeclaredIOTask(SandboxVersion = 1)]
    public class CombineLinkerXmlFiles : Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        /// <summary>
        /// The individual ILLink xml files that will be combined into one.
        /// </summary>
        [Required]
        public ITaskItem[] LinkerXmlFiles { get; set; }

        /// <summary>
        /// The path to the file to generate.
        /// </summary>
        [Required]
        public string CombinedLinkerXmlFile { get; set; }

        public ITaskItem[] DeclaredInputs { get; set; }

        public ITaskItem[] DeclaredOutputs { get; set; }

        public override bool Execute()
        {
            var combined = new XElement("linker");
            foreach (var linkerXmlFile in LinkerXmlFiles)
            {
                string path = TaskEnvironment.IsSandboxed
                    ? TaskEnvironment.GetInputPath(linkerXmlFile.ItemSpec).Value
                    : linkerXmlFile.ItemSpec;
                XDocument subFile = XDocument.Load(path);

                foreach (var element in subFile.Root.Elements())
                {
                    combined.Add(element);
                }
            }

            var xdoc = new XDocument(combined);

            XmlWriterSettings xws = new XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = true
            };

            string outputPath = TaskEnvironment.IsSandboxed
                ? TaskEnvironment.GetOutputPath(CombinedLinkerXmlFile).Value
                : CombinedLinkerXmlFile;
            using (XmlWriter xw = XmlWriter.Create(outputPath, xws))
            {
                xdoc.Save(xw);
            }

            return true;
        }
    }
}
