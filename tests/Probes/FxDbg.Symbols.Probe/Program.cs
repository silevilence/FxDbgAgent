using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.DiaSymReader;

namespace FxDbg.Symbols.Probe;

internal static class Program
{
    private const long MaximumInputBytes = 512L * 1024L * 1024L;
    private const int MaximumDocuments = 100_000;
    private const int MaximumMethods = 1_000_000;
    private const int MaximumSequencePoints = 5_000_000;
    private static readonly TimeSpan MaximumManagedProcessingTime = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static int Main(string[] args)
    {
        SymbolResult result;
        try
        {
            ProbeOptions options = ProbeOptions.Parse(args);
            result = Map(options);
        }
        catch (Exception exception)
        {
            result = SymbolResult.ReadFailed(null, null, null, 0, "unknown", exception);
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static SymbolResult Map(ProbeOptions options)
    {
        if (!File.Exists(options.PdbPath))
        {
            return SymbolResult.Failure(
                "pdb_missing",
                options,
                "unknown",
                "The requested Windows PDB file does not exist.");
        }

        string pdbFormat = "unknown";
        try
        {
            EnsureInputSize(options.AssemblyPath, "assembly");
            EnsureInputSize(options.PdbPath, "pdb");
            Stopwatch processingTimer = Stopwatch.StartNew();
            using var assemblyStream = File.OpenRead(options.AssemblyPath);
            using var peReader = new PEReader(assemblyStream);
            if (!peReader.HasMetadata)
            {
                return SymbolResult.Failure(
                    "read_failed",
                    options,
                    "unknown",
                    "The assembly has no managed metadata.");
            }

            DebugDirectoryEntry[] codeViewEntries = peReader.ReadDebugDirectory()
                .Where(entry => entry.Type == DebugDirectoryEntryType.CodeView)
                .ToArray();
            if (codeViewEntries.Length != 1)
            {
                throw new SymbolProbeException(
                    "pe_codeview_invalid",
                    $"Expected exactly one CodeView entry, but found {codeViewEntries.Length}.");
            }

            DebugDirectoryEntry codeViewEntry = codeViewEntries[0];
            CodeViewDebugDirectoryData codeView = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);
            using var pdbStream = File.OpenRead(options.PdbPath);
            pdbFormat = DetectPdbFormat(pdbStream);
            if (pdbFormat != "windows")
            {
                return SymbolResult.ReadFailed(
                    options.AssemblyPath,
                    options.PdbPath,
                    options.SourcePath,
                    options.Line,
                    pdbFormat,
                    new InvalidDataException("Input is not a Windows PDB with an MSF 7.00 header."));
            }

            ISymUnmanagedReader5? symReader = null;
            try
            {
                symReader = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(
                    pdbStream,
                    EmptyMetadataProvider.Instance,
                    SymUnmanagedReaderCreationOptions.Default);

                int matchHResult = symReader.MatchesModule(
                    codeView.Guid,
                    codeViewEntry.Stamp,
                    codeView.Age,
                    out bool matchesModule);
                Marshal.ThrowExceptionForHR(matchHResult);
                if (!matchesModule)
                {
                    return SymbolResult.Failure(
                        "pdb_mismatch",
                        options,
                        pdbFormat,
                        "The Windows PDB signature does not match the assembly CodeView entry.");
                }

                MetadataReader metadata = peReader.GetMetadataReader();
                IReadOnlyList<SymbolMapping> mappings = options.Command == "map"
                    ? FindMappings(
                        symReader,
                        metadata,
                        options.SourcePath!,
                        options.Line,
                        processingTimer)
                    : ResolveLocation(
                        symReader,
                        metadata,
                        options.MethodToken!.Value,
                        options.IlOffset!.Value,
                        processingTimer);
                return SymbolResult.Loaded(options, mappings);
            }
            finally
            {
                if (symReader is not null)
                {
                    try
                    {
                        if (symReader is ISymUnmanagedDispose disposable)
                        {
                            Marshal.ThrowExceptionForHR(disposable.Destroy());
                        }
                    }
                    finally
                    {
                        if (Marshal.IsComObject(symReader))
                        {
                            Marshal.ReleaseComObject(symReader);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is SymbolProbeException or
            BadImageFormatException or
            IOException or
            InvalidDataException or
            InvalidOperationException or
            ArgumentException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            COMException or
            DllNotFoundException or
            NotSupportedException)
        {
            return SymbolResult.ReadFailed(
                options.AssemblyPath,
                options.PdbPath,
                options.SourcePath,
                options.Line,
                pdbFormat,
                exception);
        }
    }

    private static void EnsureInputSize(string path, string kind)
    {
        long length = new FileInfo(path).Length;
        if (length > MaximumInputBytes)
        {
            throw new SymbolProbeException(
                "input_too_large",
                $"The {kind} input is {length} bytes; the limit is {MaximumInputBytes} bytes.");
        }
    }

    private static string DetectPdbFormat(Stream pdbStream)
    {
        byte[] header = new byte[24];
        int bytesRead = pdbStream.Read(header, 0, header.Length);
        pdbStream.Position = 0;
        if (bytesRead == header.Length &&
            Encoding.ASCII.GetString(header) == "Microsoft C/C++ MSF 7.00")
        {
            return "windows";
        }

        if (bytesRead >= 4 &&
            header[0] == (byte)'B' &&
            header[1] == (byte)'S' &&
            header[2] == (byte)'J' &&
            header[3] == (byte)'B')
        {
            return "portable";
        }

        return "unknown";
    }

    private static IReadOnlyList<SymbolMapping> FindMappings(
        ISymUnmanagedReader5 symReader,
        MetadataReader metadata,
        string sourcePath,
        int line,
        Stopwatch processingTimer)
    {
        string expectedPath = Path.GetFullPath(sourcePath);
        var offsetsByMethod = new Dictionary<int, SortedSet<int>>();
        int documentCount = 0;
        int methodCount = 0;
        int sequencePointCount = 0;
        foreach (ISymUnmanagedDocument document in GetDocumentsSafely(symReader, processingTimer))
        {
            EnsureBudget(processingTimer, ++documentCount, MaximumDocuments, "document_limit_exceeded");
            string documentPath = document.GetName();
            if (!string.Equals(
                Path.GetFullPath(documentPath),
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ISymUnmanagedMethod method in GetMethodsSafely(
                symReader,
                document,
                MaximumMethods - methodCount,
                processingTimer))
            {
                EnsureBudget(processingTimer, ++methodCount, MaximumMethods, "method_limit_exceeded");
                int token = method.GetToken();
                foreach (SymUnmanagedSequencePoint point in GetSequencePointsSafely(
                    method,
                    MaximumSequencePoints - sequencePointCount,
                    processingTimer))
                {
                    EnsureBudget(
                        processingTimer,
                        ++sequencePointCount,
                        MaximumSequencePoints,
                        "sequence_point_limit_exceeded");
                    if (!point.IsHidden && point.StartLine <= line && point.EndLine >= line)
                    {
                        if (!offsetsByMethod.TryGetValue(token, out SortedSet<int>? offsets))
                        {
                            offsets = new SortedSet<int>();
                            offsetsByMethod.Add(token, offsets);
                        }

                        offsets.Add(point.Offset);
                    }
                }
            }
        }

        return offsetsByMethod
            .Select(pair => new SymbolMapping(
                "0x" + pair.Key.ToString("X8"),
                ResolveMethodName(metadata, pair.Key),
                pair.Value.ToArray(),
                sourcePath,
                line,
                line))
            .OrderBy(mapping => mapping.MethodToken, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<SymbolMapping> ResolveLocation(
        ISymUnmanagedReader5 symReader,
        MetadataReader metadata,
        int methodToken,
        int ilOffset,
        Stopwatch processingTimer)
    {
        SymUnmanagedSequencePoint? bestPoint = null;
        int documentCount = 0;
        int methodCount = 0;
        int sequencePointCount = 0;
        var visitedMethods = new HashSet<int>();
        foreach (ISymUnmanagedDocument document in GetDocumentsSafely(symReader, processingTimer))
        {
            EnsureBudget(processingTimer, ++documentCount, MaximumDocuments, "document_limit_exceeded");
            foreach (ISymUnmanagedMethod method in GetMethodsSafely(
                symReader,
                document,
                MaximumMethods - methodCount,
                processingTimer))
            {
                int token = method.GetToken();
                if (!visitedMethods.Add(token))
                {
                    continue;
                }

                EnsureBudget(processingTimer, ++methodCount, MaximumMethods, "method_limit_exceeded");
                if (token != methodToken)
                {
                    continue;
                }

                foreach (SymUnmanagedSequencePoint point in GetSequencePointsSafely(
                    method,
                    MaximumSequencePoints - sequencePointCount,
                    processingTimer))
                {
                    EnsureBudget(
                        processingTimer,
                        ++sequencePointCount,
                        MaximumSequencePoints,
                        "sequence_point_limit_exceeded");
                    if (point.IsHidden || point.Offset > ilOffset)
                    {
                        continue;
                    }

                    if (bestPoint is null || point.Offset > bestPoint.Value.Offset)
                    {
                        bestPoint = point;
                    }
                }
            }
        }

        if (bestPoint is null)
        {
            return Array.Empty<SymbolMapping>();
        }

        SymUnmanagedSequencePoint resolved = bestPoint.Value;
        return new[]
        {
            new SymbolMapping(
                "0x" + methodToken.ToString("X8"),
                ResolveMethodName(metadata, methodToken),
                new[] { resolved.Offset },
                resolved.Document.GetName(),
                resolved.StartLine,
                resolved.EndLine)
        };
    }

    private static ISymUnmanagedDocument[] GetDocumentsSafely(
        ISymUnmanagedReader reader,
        Stopwatch processingTimer)
    {
        EnsureProcessingTime(processingTimer);
        Marshal.ThrowExceptionForHR(reader.GetDocuments(0, out int count, null!));
        EnsureCollectionCount(count, MaximumDocuments, "document_limit_exceeded");
        if (count == 0)
        {
            return Array.Empty<ISymUnmanagedDocument>();
        }

        var documents = new ISymUnmanagedDocument[count];
        Marshal.ThrowExceptionForHR(reader.GetDocuments(documents.Length, out int actualCount, documents));
        EnsureProcessingTime(processingTimer);
        return TrimResult(documents, actualCount, "document_count_invalid");
    }

    private static ISymUnmanagedMethod[] GetMethodsSafely(
        ISymUnmanagedReader2 reader,
        ISymUnmanagedDocument document,
        int remainingLimit,
        Stopwatch processingTimer)
    {
        EnsureProcessingTime(processingTimer);
        Marshal.ThrowExceptionForHR(reader.GetMethodsInDocument(document, 0, out int count, null!));
        EnsureCollectionCount(count, remainingLimit, "method_limit_exceeded");
        if (count == 0)
        {
            return Array.Empty<ISymUnmanagedMethod>();
        }

        var methods = new ISymUnmanagedMethod[count];
        Marshal.ThrowExceptionForHR(reader.GetMethodsInDocument(document, methods.Length, out int actualCount, methods));
        EnsureProcessingTime(processingTimer);
        return TrimResult(methods, actualCount, "method_count_invalid");
    }

    private static SymUnmanagedSequencePoint[] GetSequencePointsSafely(
        ISymUnmanagedMethod method,
        int remainingLimit,
        Stopwatch processingTimer)
    {
        EnsureProcessingTime(processingTimer);
        Marshal.ThrowExceptionForHR(method.GetSequencePointCount(out int count));
        EnsureCollectionCount(count, remainingLimit, "sequence_point_limit_exceeded");
        if (count == 0)
        {
            return Array.Empty<SymUnmanagedSequencePoint>();
        }

        var offsets = new int[count];
        var documents = new ISymUnmanagedDocument[count];
        var startLines = new int[count];
        var startColumns = new int[count];
        var endLines = new int[count];
        var endColumns = new int[count];
        Marshal.ThrowExceptionForHR(method.GetSequencePoints(
            count,
            out int actualCount,
            offsets,
            documents,
            startLines,
            startColumns,
            endLines,
            endColumns));
        EnsureProcessingTime(processingTimer);
        EnsureCollectionCount(actualCount, count, "sequence_point_count_invalid");

        var points = new SymUnmanagedSequencePoint[actualCount];
        for (int index = 0; index < actualCount; index++)
        {
            points[index] = new SymUnmanagedSequencePoint(
                offsets[index],
                documents[index],
                startLines[index],
                startColumns[index],
                endLines[index],
                endColumns[index]);
        }

        return points;
    }

    private static T[] TrimResult<T>(T[] values, int actualCount, string errorCode)
    {
        EnsureCollectionCount(actualCount, values.Length, errorCode);
        if (actualCount == values.Length)
        {
            return values;
        }

        Array.Resize(ref values, actualCount);
        return values;
    }

    private static void EnsureCollectionCount(int count, int maximumCount, string errorCode)
    {
        if (count < 0 || count > maximumCount)
        {
            throw new SymbolProbeException(
                errorCode,
                $"Native symbol data requested {count} items; the remaining limit is {maximumCount}.");
        }
    }

    private static void EnsureProcessingTime(Stopwatch processingTimer)
    {
        if (processingTimer.Elapsed > MaximumManagedProcessingTime)
        {
            throw new SymbolProbeException(
                "processing_timeout",
                $"Managed symbol processing exceeded {MaximumManagedProcessingTime.TotalSeconds} seconds.");
        }
    }

    private static void EnsureBudget(
        Stopwatch processingTimer,
        int observedCount,
        int maximumCount,
        string countErrorCode)
    {
        if (observedCount > maximumCount)
        {
            throw new SymbolProbeException(
                countErrorCode,
                $"Symbol enumeration exceeded its {maximumCount} item limit.");
        }

        EnsureProcessingTime(processingTimer);
    }

    private static string ResolveMethodName(MetadataReader metadata, int token)
    {
        EntityHandle entity = MetadataTokens.EntityHandle(token);
        if (entity.Kind != HandleKind.MethodDefinition)
        {
            return "<non-method-token:" + token.ToString("X8") + ">";
        }

        MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)entity);
        TypeDefinition type = metadata.GetTypeDefinition(method.GetDeclaringType());
        string namespaceName = metadata.GetString(type.Namespace);
        string typeName = metadata.GetString(type.Name);
        string methodName = metadata.GetString(method.Name);
        return string.IsNullOrEmpty(namespaceName)
            ? typeName + "." + methodName
            : namespaceName + "." + typeName + "." + methodName;
    }

    private sealed class EmptyMetadataProvider : ISymReaderMetadataProvider
    {
        internal static readonly EmptyMetadataProvider Instance = new();

        public unsafe bool TryGetStandaloneSignature(
            int standaloneSignatureToken,
            out byte* signature,
            out int length)
        {
            signature = null;
            length = 0;
            return false;
        }

        public bool TryGetTypeDefinitionInfo(
            int typeDefinitionToken,
            out string? namespaceName,
            out string? typeName,
            out TypeAttributes attributes)
        {
            namespaceName = null;
            typeName = null;
            attributes = default;
            return false;
        }

        public bool TryGetTypeReferenceInfo(
            int typeReferenceToken,
            out string? namespaceName,
            out string? typeName)
        {
            namespaceName = null;
            typeName = null;
            return false;
        }
    }

    private sealed record ProbeOptions(
        string Command,
        string AssemblyPath,
        string PdbPath,
        string? SourcePath,
        int Line,
        int? MethodToken,
        int? IlOffset)
    {
        internal static ProbeOptions Parse(string[] args)
        {
            if (args.Length == 0 ||
                (!string.Equals(args[0], "map", StringComparison.Ordinal) &&
                 !string.Equals(args[0], "resolve", StringComparison.Ordinal)))
            {
                throw new ArgumentException("Expected command: map or resolve.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Options must use --name value pairs.");
                }

                values.Add(args[index], args[index + 1]);
            }

            string assemblyPath = RequiredPath(values, "--assembly", mustExist: true);
            string pdbPath = RequiredPath(values, "--pdb", mustExist: false);
            if (args[0] == "map")
            {
                return new ProbeOptions(
                    args[0],
                    assemblyPath,
                    pdbPath,
                    RequiredPath(values, "--source", mustExist: true),
                    int.Parse(Required(values, "--line"), System.Globalization.CultureInfo.InvariantCulture),
                    null,
                    null);
            }

            return new ProbeOptions(
                args[0],
                assemblyPath,
                pdbPath,
                null,
                0,
                ParseInteger(Required(values, "--method-token")),
                ParseInteger(Required(values, "--il-offset")));
        }

        private static string RequiredPath(
            IReadOnlyDictionary<string, string> values,
            string name,
            bool mustExist)
        {
            string path = Path.GetFullPath(Required(values, name));
            if (mustExist && !File.Exists(path))
            {
                throw new FileNotFoundException("Required input does not exist.", path);
            }

            return path;
        }

        private static string Required(IReadOnlyDictionary<string, string> values, string name)
        {
            if (!values.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Missing required option " + name + ".");
            }

            return value;
        }

        private static int ParseInteger(string value)
        {
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(value[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
                : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed record SymbolMapping(
        string MethodToken,
        string MethodName,
        int[] IlOffsets,
        string? SourceFile,
        int? StartLine,
        int? EndLine);

    private sealed class SymbolProbeException : Exception
    {
        internal SymbolProbeException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        internal string ErrorCode { get; }
    }

    private sealed record SymbolResult(
        string Status,
        string PdbFormat,
        string? AssemblyFile,
        string? PdbFile,
        string? SourceFile,
        int Line,
        IReadOnlyList<SymbolMapping> Mappings,
        string? ErrorType,
        string? ErrorCode,
        string? HResult,
        string? Error)
    {
        internal static SymbolResult Loaded(ProbeOptions options, IReadOnlyList<SymbolMapping> mappings)
        {
            return new SymbolResult(
                "loaded",
                "windows",
                options.AssemblyPath,
                options.PdbPath,
                options.SourcePath,
                options.Line,
                mappings,
                null,
                null,
                null,
                null);
        }

        internal static SymbolResult Failure(
            string status,
            ProbeOptions options,
            string pdbFormat,
            string error)
        {
            return new SymbolResult(
                status,
                pdbFormat,
                options.AssemblyPath,
                options.PdbPath,
                options.SourcePath,
                options.Line,
                Array.Empty<SymbolMapping>(),
                null,
                null,
                null,
                error);
        }

        internal static SymbolResult ReadFailed(
            string? assemblyPath,
            string? pdbPath,
            string? sourcePath,
            int line,
            string pdbFormat,
            Exception exception)
        {
            return new SymbolResult(
                "read_failed",
                pdbFormat,
                assemblyPath,
                pdbPath,
                sourcePath,
                line,
                Array.Empty<SymbolMapping>(),
                exception.GetType().FullName,
                ClassifyError(exception),
                "0x" + exception.HResult.ToString("X8"),
                "Windows PDB reading failed: " + exception.Message);
        }

        private static string ClassifyError(Exception exception)
        {
            if (exception is SymbolProbeException probeException)
            {
                return probeException.ErrorCode;
            }

            return exception switch
            {
                DllNotFoundException => "native_reader_unavailable",
                IOException or UnauthorizedAccessException or System.Security.SecurityException => "io_error",
                _ => "malformed_or_unsupported_pdb"
            };
        }
    }
}
