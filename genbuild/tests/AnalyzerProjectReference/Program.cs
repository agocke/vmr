// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Reflection;

FileVersionInfo file = FileVersionInfo.GetVersionInfo(args[0]);
Version fileVersion = new(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart);
Console.WriteLine($"{Generated.Message};{AssemblyName.GetAssemblyName(args[0]).Version};{fileVersion}");
