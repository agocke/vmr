#!/usr/bin/env dotnet

// compare-pe.cs — Compare managed DLLs, distinguishing PE content
// differences from PDB-derived hash differences.
//
// Single-pair mode:
//   dotnet compare-pe.cs -- <file1.dll> <file2.dll> [--quiet]
//   Exit codes: 0=identical, 1=content match, 2=different, 3=error
//
// Batch mode (reads pairs from a file, one "file1\tfile2" per line):
//   dotnet compare-pe.cs -- --batch <pairs-file> <results-file>
//   Writes one line per pair to results-file: "0", "1", or "2"

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length >= 3 && args[0] == "--batch")
    return RunBatch(args[1], args[2]);

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet compare-pe.cs -- <file1> <file2> [--quiet]");
    Console.Error.WriteLine("       dotnet compare-pe.cs -- --batch <pairs-file> <results-file>");
    return 3;
}

return CompareSingle(args[0], args[1], quiet: args.Length > 2 && args[2] == "--quiet");

static int RunBatch(string pairsFile, string resultsFile)
{
    var lines = File.ReadAllLines(pairsFile);
    using var writer = new StreamWriter(resultsFile);
    foreach (var line in lines)
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;
        var parts = line.Split('\t');
        if (parts.Length != 2)
        {
            writer.WriteLine("3");
            continue;
        }
        int rc = ComparePair(parts[0], parts[1]);
        writer.WriteLine(rc);
    }
    return 0;
}

static int CompareSingle(string file1, string file2, bool quiet)
{
    if (!File.Exists(file1) || !File.Exists(file2))
    {
        Console.Error.WriteLine($"File not found: {(File.Exists(file1) ? file2 : file1)}");
        return 3;
    }

    int rc = ComparePair(file1, file2);

    if (!quiet)
    {
        var info1 = ExtractPeInfo(file1);
        var info2 = ExtractPeInfo(file2);
        var bytes1 = File.ReadAllBytes(file1);
        var bytes2 = File.ReadAllBytes(file2);

        Console.WriteLine($"File 1: {Path.GetFileName(file1)} ({bytes1.Length} bytes)");
        Console.WriteLine($"File 2: {Path.GetFileName(file2)} ({bytes2.Length} bytes)");
        Console.WriteLine();
        PrintComparison("PDB Path", info1.PdbPath, info2.PdbPath);
        PrintComparison("MVID", info1.Mvid, info2.Mvid);
        PrintComparison("TimeDateStamp", $"0x{info1.TimeDateStamp:X8}", $"0x{info2.TimeDateStamp:X8}");
        PrintComparison("CodeView GUID", info1.CodeViewGuid, info2.CodeViewGuid);
        PrintComparison("PDB Checksum", info1.PdbChecksum, info2.PdbChecksum);
        Console.WriteLine();

        Console.WriteLine(rc switch
        {
            0 => "IDENTICAL",
            1 => "CONTENT_MATCH: PE content identical after stripping PDB-derived hashes",
            _ => "DIFFERENT: PE content differs beyond PDB-derived hashes"
        });
    }

    return rc;
}

static int ComparePair(string file1, string file2)
{
    if (!File.Exists(file1) || !File.Exists(file2))
        return 3;

    var bytes1 = File.ReadAllBytes(file1);
    var bytes2 = File.ReadAllBytes(file2);

    if (bytes1.AsSpan().SequenceEqual(bytes2))
        return 0;

    try
    {
        var scrubbed1 = ZeroPdbDerivedFields(bytes1);
        var scrubbed2 = ZeroPdbDerivedFields(bytes2);
        return scrubbed1.AsSpan().SequenceEqual(scrubbed2) ? 1 : 2;
    }
    catch
    {
        return 2;
    }
}

static void PrintComparison(string label, string? val1, string? val2)
{
    var match = val1 == val2 ? "✓" : "✗";
    Console.WriteLine($"  {match} {label,-20} {val1 ?? "(null)"}");
    if (val1 != val2)
        Console.WriteLine($"    {"",-20} {val2 ?? "(null)"}");
}

static PeInfo ExtractPeInfo(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var meta = pe.GetMetadataReader();
    var mvid = meta.GetGuid(meta.GetModuleDefinition().Mvid);
    var stamp = pe.PEHeaders.CoffHeader.TimeDateStamp;

    string? pdbPath = null;
    string? cvGuid = null;
    string? pdbChecksum = null;

    foreach (var entry in pe.ReadDebugDirectory())
    {
        if (entry.Type == DebugDirectoryEntryType.CodeView)
        {
            var cv = pe.ReadCodeViewDebugDirectoryData(entry);
            pdbPath = cv.Path;
            cvGuid = cv.Guid.ToString();
        }
        else if (entry.Type == DebugDirectoryEntryType.PdbChecksum)
        {
            var ck = pe.ReadPdbChecksumDebugDirectoryData(entry);
            pdbChecksum = $"{ck.AlgorithmName}:{Convert.ToHexString(ck.Checksum.ToArray())}";
        }
    }

    return new PeInfo(mvid.ToString(), stamp, pdbPath, cvGuid, pdbChecksum);
}

static byte[] ZeroPdbDerivedFields(byte[] original)
{
    var data = (byte[])original.Clone();

    using var stream = new MemoryStream(data);
    using var pe = new PEReader(stream);

    var headers = pe.PEHeaders;
    var coffOffset = headers.PEHeaderStartOffset - 20;

    // Zero TimeDateStamp in COFF header
    Zero(data, coffOffset + 4, 4);

    // Zero MVID in #GUID heap
    var meta = pe.GetMetadataReader();
    var mvidHandle = meta.GetModuleDefinition().Mvid;
    int metadataOffset = pe.PEHeaders.MetadataStartOffset;
    int guidIndex = MetadataTokens.GetHeapOffset(mvidHandle);
    int mvidFileOffset = FindGuidHeapOffset(data, metadataOffset, guidIndex);
    if (mvidFileOffset >= 0)
        Zero(data, mvidFileOffset, 16);

    // Zero debug directory entry fields
    int debugDirOffset = FindDebugDirectoryFileOffset(pe);
    foreach (var entry in pe.ReadDebugDirectory())
    {
        if (entry.Type == DebugDirectoryEntryType.CodeView)
            Zero(data, entry.DataPointer + 4, 16); // GUID after RSDS signature
        else if (entry.Type == DebugDirectoryEntryType.PdbChecksum)
            Zero(data, entry.DataPointer, entry.DataSize);
    }

    // Zero TimeDateStamp in each debug directory table entry
    if (debugDirOffset >= 0)
    {
        int entryCount = pe.PEHeaders.PEHeader!.DebugTableDirectory.Size / 28;
        for (int i = 0; i < entryCount; i++)
            Zero(data, debugDirOffset + i * 28 + 4, 4);
    }

    return data;
}

static int FindDebugDirectoryFileOffset(PEReader pe)
{
    var debugDir = pe.PEHeaders.PEHeader!.DebugTableDirectory;
    if (debugDir.Size == 0) return -1;

    foreach (var section in pe.PEHeaders.SectionHeaders)
    {
        if (section.VirtualAddress <= debugDir.RelativeVirtualAddress &&
            debugDir.RelativeVirtualAddress < section.VirtualAddress + section.VirtualSize)
        {
            return section.PointerToRawData + (debugDir.RelativeVirtualAddress - section.VirtualAddress);
        }
    }
    return -1;
}

static int FindGuidHeapOffset(byte[] data, int metadataStart, int heapOffset)
{
    if (BitConverter.ToUInt32(data, metadataStart) != 0x424A5342)
        return -1;

    int versionLength = BitConverter.ToInt32(data, metadataStart + 12);
    int pos = metadataStart + 16 + versionLength;
    pos = (pos + 3) & ~3;

    int numStreams = BitConverter.ToUInt16(data, pos + 2);
    pos += 4;

    for (int i = 0; i < numStreams; i++)
    {
        int streamOffset = BitConverter.ToInt32(data, pos);
        int nameStart = pos + 8;
        int nameEnd = nameStart;
        while (nameEnd < data.Length && data[nameEnd] != 0) nameEnd++;
        var name = System.Text.Encoding.ASCII.GetString(data, nameStart, nameEnd - nameStart);

        if (name == "#GUID")
            return metadataStart + streamOffset + (heapOffset - 1) * 16;

        pos = (nameEnd + 4) & ~3;
    }
    return -1;
}

static void Zero(byte[] data, int offset, int length)
{
    if (offset >= 0 && offset + length <= data.Length)
        Array.Clear(data, offset, length);
}

record PeInfo(string Mvid, int TimeDateStamp, string? PdbPath, string? CodeViewGuid, string? PdbChecksum);
