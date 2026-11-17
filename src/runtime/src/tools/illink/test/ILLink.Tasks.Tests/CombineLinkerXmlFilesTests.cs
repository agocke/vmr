// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Xunit;

namespace ILLink.Tasks.Tests
{
    public class CombineLinkerXmlFilesTests
    {
        [Fact]
        public void TestCombineLinkerXmlFiles()
        {
            CreateLinkerXml(
                new XElement("linker",
                    new XElement("assembly",
                        new XAttribute("fullname", "assembly1"),
                        new XElement("type",
                            new XAttribute("fullname", "Namespace1.Type1"),
                            new XAttribute("required", "false")))),
                "doc1.xml");

            CreateLinkerXml(
                new XElement("linker",
                    new XElement("assembly",
                        new XAttribute("fullname", "assembly2"),
                        new XElement("type",
                            new XAttribute("fullname", "*"),
                            new XAttribute("required", "true")))),
                "doc2.xml");

            var xmlFiles = new ITaskItem[] {
                new TaskItem("doc1.xml"),
                new TaskItem("doc2.xml"),
            };

            var combiner = new CombineLinkerXmlFiles()
            {
                LinkerXmlFiles = xmlFiles,
                CombinedLinkerXmlFile = "combined_output.xml"
            };

            Assert.True(combiner.Execute());

            XDocument combined = XDocument.Load("combined_output.xml");

            string expectedXml = new XElement("linker",
                    new XElement("assembly",
                        new XAttribute("fullname", "assembly1"),
                        new XElement("type",
                            new XAttribute("fullname", "Namespace1.Type1"),
                            new XAttribute("required", "false"))),
                    new XElement("assembly",
                        new XAttribute("fullname", "assembly2"),
                        new XElement("type",
                            new XAttribute("fullname", "*"),
                            new XAttribute("required", "true")))).ToString();
            Assert.Equal(expectedXml, combined.Root.ToString());
        }

        [Fact]
        public void TestCombineLinkerXmlFilesUsesStagedPaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "illink-staged-combine-" + Guid.NewGuid());
            string logicalDirectory = Path.Combine(root, "logical");
            string stagedInputDirectory = Path.Combine(root, "staged-input");
            string stagedOutputDirectory = Path.Combine(root, "staged-output");
            Directory.CreateDirectory(logicalDirectory);
            Directory.CreateDirectory(stagedInputDirectory);
            Directory.CreateDirectory(stagedOutputDirectory);

            try
            {
                string logicalInput = Path.Combine(logicalDirectory, "doc.xml");
                string stagedInput = Path.Combine(stagedInputDirectory, "doc.xml");
                string logicalOutput = Path.Combine(logicalDirectory, "combined.xml");
                string stagedOutput = Path.Combine(stagedOutputDirectory, "combined.xml");
                CreateLinkerXml(new XElement("linker", new XElement("assembly", new XAttribute("fullname", "staged"))), logicalInput);
                File.Copy(logicalInput, stagedInput);

                var task = new CombineLinkerXmlFiles
                {
                    LinkerXmlFiles = new ITaskItem[] { new TaskItem(logicalInput) },
                    CombinedLinkerXmlFile = logicalOutput,
                    DeclaredInputs = new ITaskItem[] { new TaskItem(logicalInput) },
                    DeclaredOutputs = new ITaskItem[] { new TaskItem(logicalOutput) },
                    TaskEnvironment = CreateMappedEnvironment(
                        root,
                        path => Path.Combine(stagedInputDirectory, Path.GetFileName(path)),
                        path => Path.Combine(stagedOutputDirectory, Path.GetFileName(path)))
                };

                Assert.True(task.Execute());
                Assert.True(File.Exists(stagedOutput));
                Assert.False(File.Exists(logicalOutput));
                Assert.Equal("staged", XDocument.Load(stagedOutput).Root.Element("assembly").Attribute("fullname").Value);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static TaskEnvironment CreateMappedEnvironment(
            string projectDirectory,
            Func<string, string> inputPath,
            Func<string, string> outputPath)
        {
            TaskEnvironment environment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(projectDirectory);
            MethodInfo setFilePathResolvers = typeof(TaskEnvironment).GetMethod(
                "SetFilePathResolvers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(setFilePathResolvers);
            setFilePathResolvers.Invoke(environment, new object[] { inputPath, outputPath, projectDirectory, null });
            return environment;
        }

        private static void CreateLinkerXml(XElement root, string path)
        {
            var doc = new XDocument();
            doc.Add(root);
            doc.Save(path);
        }
    }
}
