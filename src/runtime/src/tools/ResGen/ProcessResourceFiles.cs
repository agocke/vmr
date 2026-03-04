// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.ComponentModel.Design;
using System.Resources.Extensions;

#nullable disable

namespace Microsoft.Build.Tasks;

/// <summary>
/// This class handles the processing of source resource files into compiled resource files.
/// Its designed to be called from a separate AppDomain so that any files locked by ResXResourceReader
/// can be released.
/// </summary>
internal sealed class ProcessResourceFiles
{
    private Logger _logger;

    /// <summary>
    /// List of readers used for input.
    /// </summary>
    private List<ReaderInfo> _readers = new List<ReaderInfo>();

    /// <summary>
    /// List of input files to process.
    /// </summary>
    private List<string> _inFiles;

    /// <summary>
    /// List of output files to process.
    /// </summary>
    private List<string> _outFiles;

    /// <summary>
    /// List of output files that we failed to create due to an error.
    /// See note in RemoveUnsuccessfullyCreatedResourcesFromOutputResources()
    /// </summary>
    internal ArrayList UnsuccessfullyCreatedOutFiles
    {
        get
        {
            _unsuccessfullyCreatedOutFiles ??= new ArrayList();
            return _unsuccessfullyCreatedOutFiles;
        }
    }
    private ArrayList _unsuccessfullyCreatedOutFiles;

    public void Run()
    {
        for (int i = 0; i < _inFiles.Count; ++i)
        {
            string outputSpec = _outFiles[i];
            if (!ProcessFile(_inFiles[i], outputSpec))
            {
                // Since we failed, remove items from OutputResources.
                UnsuccessfullyCreatedOutFiles.Add(outputSpec);
            }
        }
    }

    public ProcessResourceFiles(
        Logger logger,
        string srcPath,
        string outPath)
    {
        _logger = logger;
        _inFiles = [ srcPath ];
        _outFiles = [ outPath ];
    }

    /// <summary>
    /// Read all resources from a file and write to a new file in the chosen format
    /// </summary>
    /// <remarks>Uses the input and output file extensions to determine their format</remarks>
    /// <param name="inFile">Input resources file</param>
    /// <param name="outFileOrDir">Output resources file or directory</param>
    /// <returns>True if conversion was successful, otherwise false</returns>
    private bool ProcessFile(string inFile, string outFileOrDir)
    {
        Format inFileFormat = GetFormat(inFile);
        if (inFileFormat == Format.Error)
        {
            // GetFormat would have logged an error.
            return false;
        }
        Format outFileFormat = GetFormat(outFileOrDir);
        if (outFileFormat == Format.Error)
        {
            return false;
        }

        //_logger.LogMessageFromResources("GenerateResource.ProcessingFile", inFile, outFileOrDir);

        // Reset state
        _readers = new List<ReaderInfo>();

        try
        {
            ReadResources(inFile);
        }
        catch (InputFormatNotSupportedException)
        {
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                                                    "GenerateResource.CoreSupportsLimitedScenarios");
            return false;
        }
        catch (MSBuildResXException msbuildResXException)
        {
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", msbuildResXException.InnerException.ToString());
            return false;
        }
        catch (ArgumentException ae)
        {
            if (ae.InnerException is XmlException xe)
            {
                _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), xe.LineNumber,
                    xe.LinePosition, 0, 0, "General.InvalidResxFile", xe.Message);
            }
            else
            {
                _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                    "General.InvalidResxFile", ae.Message);
            }
            return false;
        }
        catch (TextFileException tfe)
        {
            // Used to pass back error context from ReadTextResources to here.
            _logger.LogErrorWithCodeFromResources(null, tfe.FileName, tfe.LineNumber, tfe.LinePosition, 1, 1,
                "GenerateResource.MessageTunnel", tfe.Message);
            return false;
        }
        catch (XmlException xe)
        {
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), xe.LineNumber,
                xe.LinePosition, 0, 0, "General.InvalidResxFile", xe.Message);
            return false;
        }
        catch (Exception e) when (
                                    e is SerializationException ||
                                    e is TargetInvocationException)
        {
            // DDB #9819
            // SerializationException and TargetInvocationException can occur when trying to deserialize a type from a resource format (typically with other exceptions inside)
            // This is a bug in the type being serialized, so the best we can do is dump diagnostic information and move on to the next input resource file.
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", e.Message);

            // Log the stack, so the problem with the type in the .resx is diagnosable by the customer
            _logger.LogErrorFromException(e); //, /* stack */ true, /* inner exceptions */ true, FileUtilities.GetFullPathNoThrow(inFile));
            return false;
        }
        catch (Exception e)
        {
            // Regular IO error
            _logger.LogErrorWithCodeFromResources(null, FileUtilities.GetFullPathNoThrow(inFile), 0, 0, 0, 0,
                "General.InvalidResxFile", e.Message);
            return false;
        }

        string currentOutputFile = null;
        string currentOutputDirectory = null;
        bool currentOutputDirectoryAlreadyExisted = true;

        try
        {
            currentOutputFile = outFileOrDir;
            WriteResources(_readers[0], outFileOrDir);
        }
        catch (IOException io)
        {
            if (currentOutputFile != null)
            {
                _logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                    FileUtilities.GetFullPathNoThrow(currentOutputFile), io.Message);
                if (File.Exists(currentOutputFile))
                {
                    RemoveCorruptedFile(currentOutputFile);
                }
            }

            if (currentOutputDirectory != null &&
                !currentOutputDirectoryAlreadyExisted)
            {
                // Do not annoy the user by removing an empty directory we did not create.
                try
                {
                    Directory.Delete(currentOutputDirectory); // Remove output directory if empty
                }
                catch (Exception)
                {
                    // Fail silently (we are not even checking if the call to File.Delete succeeded)
                }
            }
            return false;
        }
        catch (Exception e) when (
                                    e is SerializationException ||
                                    e is TargetInvocationException)
        {
            // DDB #9819
            // SerializationException and TargetInvocationException can occur when trying to serialize a type into a resource format (typically with other exceptions inside)
            // This is a bug in the type being serialized, so the best we can do is dump diagnostic information and move on to the next input resource file.
            _logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                FileUtilities.GetFullPathNoThrow(inFile), e.Message); // Input file is more useful to log

            // Log the stack, so the problem with the type in the .resx is diagnosable by the customer
            _logger.LogErrorFromException(e); // , /* stack */ true, /* inner exceptions */ true, FileUtilities.GetFullPathNoThrow(inFile));
            return false;
        }
        catch (Exception e)
        {
            // Regular IO error
            _logger.LogErrorWithCodeFromResources("GenerateResource.CannotWriteOutput",
                FileUtilities.GetFullPathNoThrow(currentOutputFile), e.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the resources out of the specified file and populates the
    /// resources hashtable.
    /// </summary>
    /// <param name="filename">Filename to load</param>
    /// <param name="shouldUseSourcePath">Whether to resolve paths in the
    /// resources file relative to the resources file location</param>
    /// <param name="outFileOrDir"> Output file or directory. </param>
    private void ReadResources(string filename)
    {
        Format format = GetFormat(filename);

        ReaderInfo reader = new ReaderInfo();
        _readers.Add(reader);
        switch (format)
        {
            case Format.Text:
                ReadTextResources(reader, filename);
                break;

            case Format.XML:
                // On full framework, the default is to use the longstanding
                // deserialize/reserialize approach. On Core, always use the new
                // preserialized approach.
                foreach (IResource resource in MSBuildResXReader.GetResourcesFromFile(filename, _logger))
                {
                    AddResource(reader, resource, filename, 0, 0);
                }
                break;

            case Format.Binary:
                throw new InputFormatNotSupportedException("Reading resources from binary .resources not supported on .NET Core MSBuild");

            default:
                // We should never get here, we've already checked the format
                Debug.Fail("Unknown format " + format.ToString());
                return;
        }
        _logger.LogMessageFromResources(MessageImportance.Low, "GenerateResource.ReadResourceMessage", reader.resources.Count, filename);
    }

    /// <summary>
    /// Remove a corrupted file, with error handling and a warning if we fail.
    /// </summary>
    /// <param name="filename">Full path to file to delete</param>
    private void RemoveCorruptedFile(string filename)
    {
        _logger.LogMessageFromResources("GenerateResource.CorruptOutput", FileUtilities.GetFullPathNoThrow(filename));
        try
        {
            File.Delete(filename);
        }
        catch (Exception deleteException)
        {
            _logger.LogWarningWithCodeFromResources("GenerateResource.DeleteCorruptOutputFailed", FileUtilities.GetFullPathNoThrow(filename), deleteException.Message);

            throw;
        }
    }

    /// <summary>
    /// Figure out the format of an input resources file from the extension
    /// </summary>
    /// <param name="filename">Input resources file</param>
    /// <returns>Resources format</returns>
    private Format GetFormat(string filename)
    {
        string extension;
        try
        {
            extension = Path.GetExtension(filename);
        }
        catch (ArgumentException ex)
        {
            _logger.LogErrorWithCodeFromResources("GenerateResource.InvalidFilename", filename, ex.Message);
            return Format.Error;
        }

        if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".restext", StringComparison.OrdinalIgnoreCase))
        {
            return Format.Text;
        }
        else if (string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".resw", StringComparison.OrdinalIgnoreCase))
        {
            return Format.XML;
        }
        else if (string.Equals(extension, ".resources", StringComparison.OrdinalIgnoreCase))
        {
            return Format.Binary;
        }
        else
        {
            _logger.LogErrorWithCodeFromResources("GenerateResource.UnknownFileExtension", Path.GetExtension(filename), filename);
            return Format.Error;
        }
    }

    /// <summary>
    /// Text files are just name/value pairs.  ResText is the same format
    /// with a unique extension to work around some ambiguities with MSBuild
    /// ResX is our existing XML format from V1.
    /// </summary>
    private enum Format
    {
        Text, // .txt or .restext
        XML, // .resx
        Binary, // .resources
        Error, // anything else
    }


    /// <summary>
    /// Write resources from the resources ArrayList to the specified output file
    /// </summary>
    /// <param name="reader">Reader information</param>
    /// <param name="filename">Output resources file</param>
    private void WriteResources(ReaderInfo reader, string filename)
    {
        Format format = GetFormat(filename);
        switch (format)
        {
            case Format.Text:
                WriteTextResources(reader, filename);
                break;

            case Format.XML:
                _logger.LogError(format.ToString() + " not supported on .NET Core MSBuild");
                break;

            case Format.Binary:
                WriteBinaryResources(reader, filename);
                break;

            default:
                // We should never get here, we've already checked the format
                Debug.Fail("Unknown format " + format.ToString());
                break;
        }
    }

    private void WriteBinaryResources(ReaderInfo reader, string filename)
    {
        try
        {
            WriteResources(reader, new PreserializedResourceWriter(new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))); // closes writer for us
        }
        catch
        {
            // We may have partially written some string resources to a file, then bailed out
            // because we encountered a non-string resource but don't meet the prereqs.
            RemoveCorruptedFile(filename);
        }
    }


    /// <summary>
    /// Read resources from a text format file.
    /// </summary>
    /// <param name="reader">Reader info.</param>
    /// <param name="fileName">Input resources filename.</param>
    private void ReadTextResources(ReaderInfo reader, string fileName)
    {
        // Check for byte order marks in the beginning of the input file, but
        // default to UTF-8.
        using var fs = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (LineNumberStreamReader sr = new LineNumberStreamReader(fs, new UTF8Encoding(true), true))
        {
            StringBuilder name = new StringBuilder(255);
            StringBuilder value = new StringBuilder(2048);

            int ch = sr.Read();
            while (ch != -1)
            {
                if (ch == '\n' || ch == '\r')
                {
                    ch = sr.Read();
                    continue;
                }

                // Skip over commented lines or ones starting with whitespace.
                // Support LocStudio INF format's comment char, ';'
                if (ch == '#' || ch == '\t' || ch == ' ' || ch == ';')
                {
                    // comment char (or blank line) - skip line.
                    sr.ReadLine();
                    ch = sr.Read();
                    continue;
                }
                // Note that in Beta of version 1 we recommended users should put a [strings]
                // section in their file.  Now it's completely unnecessary and can
                // only cause bugs.  We will not parse anything using '[' stuff now
                // and we should give a warning about seeing [strings] stuff.
                // In V1.1 or V2, we can rip this out completely, I hope.
                if (ch == '[')
                {
                    string skip = sr.ReadLine();
                    if (skip.Equals("strings]"))
                    {
                        _logger.LogWarningWithCodeFromResources(null, fileName, sr.LineNumber - 1, 1, 0, 0, "GenerateResource.ObsoleteStringsTag");
                    }
                    else
                    {
                        throw new TextFileException(_logger.FormatResourceString("GenerateResource.UnexpectedInfBracket", "[" + skip), fileName, sr.LineNumber - 1, 1);
                    }
                    ch = sr.Read();
                    continue;
                }

                // Read in name
                name.Length = 0;
                while (ch != '=')
                {
                    if (ch == '\r' || ch == '\n')
                    {
                        throw new TextFileException(_logger.FormatResourceString("GenerateResource.NoEqualsInLine", name), fileName, sr.LineNumber, sr.LinePosition);
                    }

                    name.Append((char)ch);
                    ch = sr.Read();
                    if (ch == -1)
                    {
                        break;
                    }
                }
                if (name.Length == 0)
                {
                    throw new TextFileException(_logger.FormatResourceString("GenerateResource.NoNameInLine"), fileName, sr.LineNumber, sr.LinePosition);
                }

                // For the INF file, we must allow a space on both sides of the equals
                // sign.  Deal with it.
                if (name[name.Length - 1] == ' ')
                {
                    name.Length--;
                }
                ch = sr.Read(); // move past =

                // If it exists, move past the first space after the equals sign.
                if (ch == ' ')
                {
                    ch = sr.Read();
                }

                // Read in value
                value.Length = 0;

                while (ch != -1)
                {
                    // Did we read @"\r" or @"\n"?
                    bool quotedNewLine = false;
                    if (ch == '\\')
                    {
                        ch = sr.Read();
                        switch (ch)
                        {
                            case '\\':
                                // nothing needed
                                break;
                            case 'n':
                                ch = '\n';
                                quotedNewLine = true;
                                break;
                            case 'r':
                                ch = '\r';
                                quotedNewLine = true;
                                break;
                            case 't':
                                ch = '\t';
                                break;
                            case '"':
                                ch = '\"';
                                break;
                            case 'u':
                                char[] hex = new char[4];
                                int numChars = 4;
                                int index = 0;
                                while (numChars > 0)
                                {
                                    int n = sr.Read(hex, index, numChars);
                                    if (n == 0)
                                    {
                                        throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidEscape", name.ToString(), (char)ch), fileName, sr.LineNumber, sr.LinePosition);
                                    }

                                    index += n;
                                    numChars -= n;
                                }
                                try
                                {
                                    ch = (char)ushort.Parse(new string(hex), NumberStyles.HexNumber, CultureInfo.CurrentCulture);
                                }
                                catch (FormatException)
                                {
                                    // We know about this one...
                                    throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidHexEscapeValue", name.ToString(), new string(hex)), fileName, sr.LineNumber, sr.LinePosition);
                                }
                                catch (OverflowException)
                                {
                                    // We know about this one, too...
                                    throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidHexEscapeValue", name.ToString(), new string(hex)), fileName, sr.LineNumber, sr.LinePosition);
                                }
                                quotedNewLine = (ch == '\n' || ch == '\r');
                                break;

                            default:
                                throw new TextFileException(_logger.FormatResourceString("GenerateResource.InvalidEscape", name.ToString(), (char)ch), fileName, sr.LineNumber, sr.LinePosition);
                        }
                    }

                    // Consume endline...
                    //   Endline can be \r\n or \n.  But do not treat a
                    //   quoted newline (ie, @"\r" or @"\n" in text) as a
                    //   real new line.  They aren't the end of a line.
                    if (!quotedNewLine)
                    {
                        if (ch == '\r')
                        {
                            ch = sr.Read();
                            if (ch == -1)
                            {
                                break;
                            }
                            else if (ch == '\n')
                            {
                                ch = sr.Read();
                                break;
                            }
                        }
                        else if (ch == '\n')
                        {
                            ch = sr.Read();
                            break;
                        }
                    }

                    value.Append((char)ch);
                    ch = sr.Read();
                }

                // Note that value can be an empty string
                AddResource(reader, name.ToString(), value.ToString(), fileName, sr.LineNumber, sr.LinePosition);
            }
        }
    }

    /// <summary>
    /// Write resources to an XML or binary format resources file.
    /// </summary>
    /// <remarks>Closes writer automatically</remarks>
    /// <param name="reader">Reader information</param>
    /// <param name="writer">Appropriate IResourceWriter</param>
    private void WriteResources(ReaderInfo reader, PreserializedResourceWriter writer)
    {
        Exception capturedException = null;
        try
        {
            foreach (IResource entry in reader.resources)
            {
                entry.AddTo(writer);
            }
        }
        catch (Exception e)
        {
            capturedException = e; // Rethrow this after catching exceptions thrown by Close().
        }
        finally
        {
            if (capturedException == null)
            {
                writer.Dispose(); // If this throws, exceptions will be caught upstream.
            }
            else
            {
                // It doesn't hurt to call Close() twice. In the event of a full disk, we *need* to call Close() twice.
                // In that case, the first time we catch an exception indicating that the XML written to disk is malformed,
                // specifically an InvalidOperationException: "Token EndElement in state Error would result in an invalid XML document."
                try { writer.Dispose(); }
                catch (Exception) { } // We aggressively catch all exception types since we already have one we will throw.

                // The second time we catch the out of disk space exception.
                try { writer.Dispose(); }
                catch (Exception) { } // We aggressively catch all exception types since we already have one we will throw.
                throw capturedException; // In the event of a full disk, this is an out of disk space IOException.
            }
        }
    }

    /// <summary>
    /// Write resources to a text format resources file
    /// </summary>
    /// <param name="reader">Reader information</param>
    /// <param name="fileName">Output resources file</param>
    private void WriteTextResources(ReaderInfo reader, string fileName)
    {
        using (StreamWriter writer = FileUtilities.OpenWrite(fileName, false, Encoding.UTF8))
        {
            foreach (IResource resource in reader.resources)
            {
                LiveObjectResource entry = resource as LiveObjectResource;

                string key = entry?.Name;
                object v = entry?.Value;
                string value = v as string;
                if (value == null)
                {
                    _logger.LogErrorWithCodeFromResources(null, fileName, 0, 0, 0, 0, "GenerateResource.OnlyStringsSupported", key, v?.GetType().FullName);
                }
                else
                {
                    // Escape any special characters in the String.
                    value = value.Replace("\\", "\\\\");
                    value = value.Replace("\n", "\\n");
                    value = value.Replace("\r", "\\r");
                    value = value.Replace("\t", "\\t");

                    writer.WriteLine("{0}={1}", key, value);
                }
            }
        }
    }

    /// <summary>
    /// Add a resource from a text file to the internal data structures
    /// </summary>
    /// <param name="reader">Reader information</param>
    /// <param name="name">Resource name</param>
    /// <param name="value">Resource value</param>
    /// <param name="inputFileName">Input file for messages</param>
    /// <param name="lineNumber">Line number for messages</param>
    /// <param name="linePosition">Column number for messages</param>
    private void AddResource(ReaderInfo reader, string name, object value, string inputFileName, int lineNumber, int linePosition)
    {
        LiveObjectResource entry = new LiveObjectResource(name, value);

        AddResource(reader, entry, inputFileName, lineNumber, linePosition);
    }

    private void AddResource(ReaderInfo reader, IResource entry, string inputFileName, int lineNumber, int linePosition)
    {
        if (reader.resourcesHashTable.ContainsKey(entry.Name))
        {
            _logger.LogWarningWithCodeFromResources(null, inputFileName, lineNumber, linePosition, 0, 0, "GenerateResource.DuplicateResourceName", entry.Name);
            return;
        }

        reader.resources.Add(entry);
        reader.resourcesHashTable.Add(entry.Name, entry);
    }

    internal sealed class ReaderInfo
    {
        // We use a list to preserve the resource ordering (primarily for easier testing),
        // but also use a hash table to check for duplicate names.
        public readonly List<IResource> resources = new List<IResource>();
        public readonly Dictionary<string, IResource> resourcesHashTable = new Dictionary<string, IResource>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Custom StreamReader that provides detailed position information,
    /// used when reading text format resources
    /// </summary>
    internal sealed class LineNumberStreamReader : StreamReader
    {
        // Line numbers start from 1, as well as line position.
        // For better error reporting, set line number to 1 and col to 0.
        private int _lineNumber;
        private int _col;

        internal LineNumberStreamReader(Stream fileStream, Encoding encoding, bool detectEncoding)
            : base(fileStream, encoding, detectEncoding)
        {
            _lineNumber = 1;
            _col = 0;
        }

        public override int Read()
        {
            int ch = base.Read();
            if (ch != -1)
            {
                _col++;
                if (ch == '\n')
                {
                    _lineNumber++;
                    _col = 0;
                }
            }
            return ch;
        }

        public override int Read([In, Out] char[] chars, int index, int count)
        {
            int r = base.Read(chars, index, count);
            for (int i = 0; i < r; i++)
            {
                if (chars[i + index] == '\n')
                {
                    _lineNumber++;
                    _col = 0;
                }
                else
                {
                    _col++;
                }
            }
            return r;
        }

        public override string ReadLine()
        {
            string s = base.ReadLine();
            if (s != null)
            {
                _lineNumber++;
                _col = 0;
            }
            return s;
        }

        public override string ReadToEnd()
        {
            throw new NotImplementedException("NYI");
        }

        internal int LineNumber
        {
            get { return _lineNumber; }
        }

        internal int LinePosition
        {
            get { return _col; }
        }
    }

    /// <summary>
    /// For flow of control and passing sufficient error context back
    /// from ReadTextResources
    /// </summary>
    internal sealed class TextFileException : Exception
    {
        internal string FileName { get; }

        internal int LineNumber { get; }
        internal int LinePosition { get; }

        internal TextFileException(string message, string fileName, int lineNumber, int linePosition)
            : base(message)
        {
            FileName = fileName;
            LineNumber = lineNumber;
            LinePosition = linePosition;
        }
    }
}
