using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using FxDbg.Core.Breakpoints;
using FxDbg.Core.Model;
using Microsoft.DiaSymReader;

namespace FxDbg.Symbols.Windows;

/// <summary>A local Windows PDB reader. Native work must be isolated in an Engine process.</summary>
public sealed class WindowsModuleSymbols : IDisposable
{
    private const int MaximumItems = 1_000_000;
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private readonly Dictionary<string, List<BreakpointBindingLocation>> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> nativeObjects = new();
    private PEReader? pe;
    private Stream? assemblyStream;
    private Stream? pdbStream;
    private ISymUnmanagedReader5? reader;
    private bool disposed;

    private WindowsModuleSymbols(string assemblyPath, string pdbPath)
    {
        AssemblyPath = assemblyPath;
        PdbPath = pdbPath;
    }

    public string AssemblyPath { get; }
    public string PdbPath { get; }
    public SymbolStatus Status { get; private set; }
    public string? Diagnostic { get; private set; }

    public static WindowsModuleSymbols Open(string assemblyPath, string? pdbPath = null)
    {
        var symbols = new WindowsModuleSymbols(Path.GetFullPath(assemblyPath),
            Path.GetFullPath(pdbPath ?? Path.ChangeExtension(assemblyPath, ".pdb")));
        try
        {
            symbols.Load();
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException ||
            exception is BadImageFormatException || exception is COMException || exception is ArgumentException ||
            exception is InvalidOperationException || exception is NotSupportedException || exception is DllNotFoundException)
        {
            symbols.Status = SymbolStatus.ReadFailed;
            symbols.Diagnostic = "Cannot read local Windows PDB: " + exception.Message;
            symbols.CloseReader();
            symbols.documents.Clear();
        }
        return symbols;
    }

    public SourceBreakpointResolution Resolve(SourceLocation location)
    {
        ThrowIfDisposed();
        if (!documents.TryGetValue(Path.GetFullPath(location.FilePath), out List<BreakpointBindingLocation>? points))
            return new SourceBreakpointResolution(false, Array.Empty<BreakpointBindingLocation>(), Diagnostic);
        if (points.Count == 0)
            return new SourceBreakpointResolution(true, Array.Empty<BreakpointBindingLocation>(), "The source contains no executable sequence points.");
        int line = points.OrderBy(point => Math.Abs((long)point.Source.Line - location.Line))
            .ThenBy(point => point.Source.Line).First().Source.Line;
        return new SourceBreakpointResolution(true, points.Where(point => point.Source.Line == line).ToArray(), null);
    }

    public SourceLocation? Resolve(int methodToken, int ilOffset)
    {
        ThrowIfDisposed();
        return documents.Values.SelectMany(points => points)
            .Where(point => point.MethodToken == methodToken && point.IlOffset <= ilOffset)
            .OrderByDescending(point => point.IlOffset).FirstOrDefault()?.Source;
    }

    public int GetStepRangeEnd(int methodToken, int ilOffset, int codeSize)
    {
        ThrowIfDisposed();
        SourceLocation? current = Resolve(methodToken, ilOffset);
        return documents.Values.SelectMany(points => points)
            .Where(point => point.MethodToken == methodToken && point.IlOffset > ilOffset &&
                (current is null || point.Source.Line != current.Line || point.Source.FilePath != current.FilePath))
            .Select(point => point.IlOffset).DefaultIfEmpty(codeSize).Min();
    }

    public IReadOnlyList<LocalVariableSlot> GetLocals(int methodToken, int ilOffset)
    {
        ThrowIfDisposed();
        var result = new List<LocalVariableSlot>();
        if (reader is null) return result;
        int hr = reader.GetMethod(methodToken, out ISymUnmanagedMethod method);
        if (hr < 0) return result;
        try
        {
            Marshal.ThrowExceptionForHR(method.GetRootScope(out ISymUnmanagedScope root));
            int scopes = 0;
            try { ReadLocals(root, ilOffset, result, ref scopes, 0); }
            finally { ReleaseCom(root); }
        }
        finally { ReleaseCom(method); }
        return result;
    }

    private static void ReadLocals(ISymUnmanagedScope scope, int offset, List<LocalVariableSlot> result, ref int scopes, int depth)
    {
        if (++scopes > 10000 || depth > 128) throw new InvalidDataException("PDB local scopes exceed their traversal budget.");
        Marshal.ThrowExceptionForHR(scope.GetStartOffset(out int start));
        Marshal.ThrowExceptionForHR(scope.GetEndOffset(out int end));
        if (offset < start || offset >= end) return;
        Marshal.ThrowExceptionForHR(scope.GetLocalCount(out int count));
        CheckCount(count, 10000 - result.Count);
        var locals = new ISymUnmanagedVariable[count];
        try
        {
            Marshal.ThrowExceptionForHR(scope.GetLocals(count, out int actual, locals));
            CheckCount(actual, count);
            foreach (ISymUnmanagedVariable local in locals.Take(actual))
            {
                Marshal.ThrowExceptionForHR(local.GetName(0, out int length, null!));
                CheckCount(length, 32768);
                var chars = new char[length];
                Marshal.ThrowExceptionForHR(local.GetName(length, out int nameLength, chars));
                CheckCount(nameLength, length);
                Marshal.ThrowExceptionForHR(local.GetAddressField1(out int slot));
                result.Add(new LocalVariableSlot(new string(chars, 0, nameLength).TrimEnd('\0'), slot));
            }
        }
        finally { foreach (ISymUnmanagedVariable local in locals) ReleaseCom(local); }
        Marshal.ThrowExceptionForHR(scope.GetChildren(0, out int childCount, null!));
        CheckCount(childCount, 10000 - scopes);
        var children = new ISymUnmanagedScope[childCount];
        try
        {
            Marshal.ThrowExceptionForHR(scope.GetChildren(childCount, out int childActual, children));
            CheckCount(childActual, childCount);
            foreach (ISymUnmanagedScope child in children.Take(childActual)) ReadLocals(child, offset, result, ref scopes, depth + 1);
        }
        finally { foreach (ISymUnmanagedScope child in children) ReleaseCom(child); }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CloseReader();
        documents.Clear();
    }

    private void Load()
    {
        if (!File.Exists(PdbPath))
        {
            Status = SymbolStatus.Missing;
            Diagnostic = "Local Windows PDB is missing: " + PdbPath;
            return;
        }
        if (new FileInfo(AssemblyPath).Length > MaximumInputBytes || new FileInfo(PdbPath).Length > MaximumInputBytes)
            throw new InvalidDataException("PE/PDB exceeds the 512 MiB input limit.");
        var timer = Stopwatch.StartNew();
        assemblyStream = File.OpenRead(AssemblyPath);
        pe = new PEReader(assemblyStream);
        MetadataReader metadata = pe.GetMetadataReader();
        DebugDirectoryEntry[] entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
        if (entries.Length != 1) throw new InvalidDataException("Expected one PE CodeView record.");
        CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(entries[0]);
        pdbStream = new FileStream(PdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[24];
        if (pdbStream.Read(header, 0, header.Length) != header.Length || Encoding.ASCII.GetString(header) != "Microsoft C/C++ MSF 7.00")
            throw new InvalidDataException("Only Windows PDB (MSF 7.00) is supported.");
        pdbStream.Position = 0;
        reader = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(pdbStream,
            new MetadataProvider(metadata), SymUnmanagedReaderCreationOptions.Default);
        Marshal.ThrowExceptionForHR(reader.MatchesModule(codeView.Guid, entries[0].Stamp, codeView.Age, out bool matches));
        if (!matches)
        {
            Status = SymbolStatus.Mismatch;
            Diagnostic = "Windows PDB signature does not match the module CodeView identity.";
            CloseReader();
            return;
        }
        Marshal.ThrowExceptionForHR(reader.GetDocuments(0, out int count, null!));
        CheckCount(count, 100_000);
        var docs = new ISymUnmanagedDocument[count];
        Marshal.ThrowExceptionForHR(reader.GetDocuments(count, out int actual, docs));
        CheckCount(actual, count);
        nativeObjects.AddRange(docs.Take(actual));
        int totalMethods = 0;
        int totalPoints = 0;
        var visitedMethods = new HashSet<int>();
        for (int index = 0; index < actual; index++)
        {
            CheckTime(timer);
            ISymUnmanagedDocument document = docs[index];
            string path = ReadDocumentName(document);
            if (!documents.ContainsKey(path)) documents.Add(path, new List<BreakpointBindingLocation>());
            Marshal.ThrowExceptionForHR(reader.GetMethodsInDocument(document, 0, out int methodCount, null!));
            CheckCount(methodCount, MaximumItems - totalMethods);
            var methods = new ISymUnmanagedMethod[methodCount];
            Marshal.ThrowExceptionForHR(reader.GetMethodsInDocument(document, methodCount, out int methodActual, methods));
            CheckCount(methodActual, methodCount);
            totalMethods += methodActual;
            nativeObjects.AddRange(methods.Take(methodActual));
            foreach (ISymUnmanagedMethod method in methods.Take(methodActual))
            {
                CheckTime(timer);
                int token = method.GetToken();
                if (!visitedMethods.Add(token)) continue;
                Marshal.ThrowExceptionForHR(method.GetSequencePointCount(out int pointCount));
                CheckCount(pointCount, MaximumItems - totalPoints);
                totalPoints += pointCount;
                var offsets = new int[pointCount];
                var pointDocs = new ISymUnmanagedDocument[pointCount];
                var lines = new int[pointCount];
                var columns = new int[pointCount];
                var endLines = new int[pointCount];
                var endColumns = new int[pointCount];
                Marshal.ThrowExceptionForHR(method.GetSequencePoints(pointCount, out int pointActual,
                    offsets, pointDocs, lines, columns, endLines, endColumns));
                CheckCount(pointActual, pointCount);
                nativeObjects.AddRange(pointDocs.Take(pointActual));
                for (int point = 0; point < pointActual; point++)
                {
                    CheckTime(timer);
                    if (lines[point] == 0xfeefee || lines[point] <= 0) continue;
                    string source = ReadDocumentName(pointDocs[point]);
                    if (!documents.TryGetValue(source, out List<BreakpointBindingLocation>? locations))
                    {
                        locations = new List<BreakpointBindingLocation>();
                        documents.Add(source, locations);
                    }
                    locations.Add(new BreakpointBindingLocation(token, offsets[point], new SourceLocation(source, lines[point], columns[point])));
                }
            }
        }
        Status = SymbolStatus.Loaded;
    }

    private static string ReadDocumentName(ISymUnmanagedDocument document)
    {
        Marshal.ThrowExceptionForHR(document.GetUrl(0, out int count, null!));
        CheckCount(count, 32768);
        var chars = new char[count];
        Marshal.ThrowExceptionForHR(document.GetUrl(count, out int actual, chars));
        CheckCount(actual, count);
        return Path.GetFullPath(new string(chars, 0, actual).TrimEnd('\0'));
    }

    private static void CheckCount(int count, int maximum)
    {
        if (count < 0 || count > maximum) throw new InvalidDataException("PDB collection exceeds its allocation budget.");
    }

    private static void CheckTime(Stopwatch timer)
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidDataException("PDB processing exceeded 30 seconds.");
    }

    private void CloseReader()
    {
        try
        {
            foreach (object value in nativeObjects) if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            nativeObjects.Clear();
            if (reader is ISymUnmanagedDispose disposable) Marshal.ThrowExceptionForHR(disposable.Destroy());
        }
        finally
        {
            if (reader is not null && Marshal.IsComObject(reader)) Marshal.ReleaseComObject(reader);
            reader = null;
            pe?.Dispose();
            pe = null;
            assemblyStream?.Dispose();
            assemblyStream = null;
            pdbStream?.Dispose();
            pdbStream = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WindowsModuleSymbols));
    }

    private sealed class MetadataProvider : ISymReaderMetadataProvider
    {
        private readonly MetadataReader metadata;
        internal MetadataProvider(MetadataReader metadata) => this.metadata = metadata;

        public unsafe bool TryGetStandaloneSignature(int token, out byte* signature, out int length)
        {
            signature = null;
            length = 0;
            if ((token & unchecked((int)0xff000000)) != 0x11000000) return false;
            BlobReader blob = metadata.GetBlobReader(metadata.GetStandaloneSignature((StandaloneSignatureHandle)MetadataTokens.Handle(token)).Signature);
            signature = blob.StartPointer;
            length = blob.Length;
            return true;
        }

        public bool TryGetTypeDefinitionInfo(int token, out string? namespaceName, out string? typeName, out TypeAttributes attributes)
        {
            TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(token));
            namespaceName = metadata.GetString(type.Namespace);
            typeName = metadata.GetString(type.Name);
            attributes = type.Attributes;
            return true;
        }

        public bool TryGetTypeReferenceInfo(int token, out string? namespaceName, out string? typeName)
        {
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)MetadataTokens.Handle(token));
            namespaceName = metadata.GetString(type.Namespace);
            typeName = metadata.GetString(type.Name);
            return true;
        }
    }
}
