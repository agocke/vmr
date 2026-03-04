// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Tasks;

string? srcPath = null;
string? outPath = null;
for (int i = 0; i < args.Length; i++)
{
    var arg = args[i].TrimStart('\'').TrimEnd('\'');
    if (StartsWith(arg, "--src-path=", out var temp))
    {
        srcPath = temp;
    }
    else if(StartsWith(arg, "--out-path=", out var temp2))
    {
        outPath = temp2;
    }
}
if (srcPath == null || outPath == null)
{
    Console.WriteLine("Usage: ResGen --src-path=<srcPath> --out-path=<outPath>");
    return 1;
}

var logger = new Logger();
var process = new ProcessResourceFiles(
    logger,
    srcPath,
    outPath);
process.Run();

return logger.HasLoggedErrors ? 2 : 0;

static bool StartsWith(string s, string prefix, [NotNullWhen(true)] out string? rest)
{
    if (s.StartsWith(prefix))
    {
        rest = s[prefix.Length..];
        return true;
    }
    rest = null;
    return false;
}
