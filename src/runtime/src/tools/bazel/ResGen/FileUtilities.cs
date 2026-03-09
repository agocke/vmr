// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.IO;
using System.Text;

internal static class FileUtilities
{
    public static string GetFullPathNoThrow(string path)
        => Path.GetFullPath(path);

    /// <summary>
    /// Extracts the directory from the given file-spec.
    /// </summary>
    /// <param name="fileSpec">The filespec.</param>
    /// <returns>directory path</returns>
    internal static string GetDirectory(string fileSpec)
    {
        string directory = Path.GetDirectoryName(FixFilePath(fileSpec));

        // if file-spec is a root directory e.g. c:, c:\, \, \\server\share
        // NOTE: Path.GetDirectoryName also treats invalid UNC file-specs as root directories e.g. \\, \\server
        if (directory == null)
        {
            // just use the file-spec as-is
            directory = fileSpec;
        }
        else if ((directory.Length > 0) && !EndsWithSlash(directory))
        {
            // restore trailing slash if Path.GetDirectoryName has removed it (this happens with non-root directories)
            directory += Path.DirectorySeparatorChar;
        }

        return directory;
    }


    /// <summary>
    /// Indicates if the given file-spec ends with a slash.
    /// </summary>
    /// <param name="fileSpec">The file spec.</param>
    /// <returns>true, if file-spec has trailing slash</returns>
    internal static bool EndsWithSlash(string fileSpec)
    {
        return (fileSpec.Length > 0)
            ? IsSlash(fileSpec[fileSpec.Length - 1])
            : false;
    }


    /// <summary>
    /// Indicates if the given character is a slash.
    /// </summary>
    /// <param name="c"></param>
    /// <returns>true, if slash</returns>
    internal static bool IsSlash(char c)
    {
        return (c == Path.DirectorySeparatorChar) || (c == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Gets the canonicalized full path of the provided path.
    /// Guidance for use: call this on all paths accepted through public entry
    /// points that need normalization. After that point, only verify the path
    /// is rooted, using ErrorUtilities.VerifyThrowPathRooted.
    /// ASSUMES INPUT IS ALREADY UNESCAPED.
    /// </summary>
    internal static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return FixFilePath(fullPath);
    }

    internal static string FixFilePath(string path)
    {
        return string.IsNullOrEmpty(path) || Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/'); // .Replace("//", "/");
    }

    internal static StreamWriter OpenWrite(string path, bool append, Encoding encoding = null)
    {
        const int DefaultFileStreamBufferSize = 4096;
        FileMode mode = append ? FileMode.Append : FileMode.Create;
        Stream fileStream = new FileStream(path, mode, FileAccess.Write, FileShare.Read, DefaultFileStreamBufferSize, FileOptions.SequentialScan);
        if (encoding == null)
        {
            return new StreamWriter(fileStream);
        }
        else
        {
            return new StreamWriter(fileStream, encoding);
        }
    }
}
