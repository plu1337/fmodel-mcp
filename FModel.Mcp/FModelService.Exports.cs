using System.Text;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using Newtonsoft.Json;

namespace FModel.Mcp;

public sealed partial class FModelService
{
    public async Task<object> StartExport(string sessionId, ExportRequest request, CancellationToken ct)
    {
        ValidateExport(request);
        var session = Session(sessionId);
        await InSession(sessionId, s =>
        {
            foreach (var path in request.Paths) { var f = File(s, path); CheckSize(f); PathPolicy.ValidateRelative(f.Path); }
            return true;
        }, ct);
        lock (_jobsSync)
        {
            if (session.Closed) throw new InvalidOperationException("Session is closed.");
            if (_jobs.Count >= _options.MaxJobs)
            {
                var oldest = _jobs.Values.Where(j => IsTerminal(j) && j.Task?.IsCompleted == true).OrderBy(j => j.CreatedAt).FirstOrDefault();
                if (oldest is null) throw new InvalidOperationException("Job limit reached. Wait for an existing export to finish.");
                _jobs.TryRemove(oldest.Id, out _); oldest.Cancellation.Dispose();
            }
            var job = new ExportJob(Guid.NewGuid().ToString("N"), sessionId, _paths.NewOutputDirectory("export"), request.Paths.Length);
            _jobs[job.Id] = job;
            job.Task = Task.Run(() => RunExport(job, session, request));
            return job.Snapshot();
        }
    }

    public object ListJobs() => new { jobs = _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(j => j.Snapshot()).ToArray() };
    public object GetJob(string id) => Job(id).Snapshot();
    public object JobResults(string id, int offset, int limit)
    {
        var job = Job(id);
        lock (job.Sync) return new { status = job.Snapshot(), results = Paginate(job.Results.ToArray(), offset, limit) };
    }
    public object CancelJob(string id)
    {
        lock (_jobsSync)
        {
            var job = Job(id);
            lock (job.Sync)
            {
                if (!job.Terminal) { job.State = "cancelling"; job.Cancellation.Cancel(); }
                return job.Snapshot();
            }
        }
    }

    public object ReadOutput(string jobId, string relativePath, string encoding, int offset, int count)
    {
        var job = Job(jobId);
        if (!IsTerminal(job)) throw new InvalidOperationException("Wait for terminal job status before reading its output.");
        PathPolicy.ValidateRelative(relativePath);
        var path = Path.GetFullPath(Path.Combine(job.OutputDirectory, relativePath));
        if (!PathPolicy.IsWithin(job.OutputDirectory, path)) throw new ArgumentException("Output path escapes job directory.");
        PathPolicy.RejectLinks(path);
        if (offset < 0 || count is < 1 or > 32768) throw new ArgumentException("offset must be nonnegative; count must be 1..32768 bytes.");
        if (encoding is not ("utf8" or "base64")) throw new ArgumentException("encoding must be utf8 or base64.");
        using var stream = System.IO.File.OpenRead(path);
        if (offset > stream.Length) throw new ArgumentException("offset is beyond file length.");
        stream.Position = offset;
        var bytes = new byte[(int)Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(bytes);
        return new { path, offset, totalBytes = stream.Length, bytesRead = bytes.Length, encoding,
            nextOffset = offset + (long)bytes.Length < stream.Length ? (int?)(offset + bytes.Length) : null,
            text = encoding == "base64" ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes) };
    }

    private async Task RunExport(ExportJob job, GameSession session, ExportRequest request)
    {
        var ct = job.Cancellation.Token;
        var locked = false;
        var finalState = "failed";
        try
        {
            await session.Gate.WaitAsync(ct); locked = true;
            if (session.Closed) throw new InvalidOperationException("Session is closed.");
            lock (job.Sync) { ct.ThrowIfCancellationRequested(); job.State = "running"; }
            foreach (var path in request.Paths)
            {
                ct.ThrowIfCancellationRequested();
                lock (job.Sync) job.CurrentPath = path;
                ExportItem result;
                try { result = await ExportOne(session, request, path, job.OutputDirectory, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { result = new(path, false, [], ErrorMessage(ex)); }
                lock (job.Sync) job.Results.Add(result);
            }
            lock (job.Sync)
            {
                ct.ThrowIfCancellationRequested();
                finalState = job.Results.All(r => r.Success) ? "completed" : "completed_with_errors";
            }
        }
        catch (OperationCanceledException) { finalState = "cancelled"; }
        catch (Exception ex) { lock (job.Sync) { finalState = "failed"; job.Error = ErrorMessage(ex); } }
        finally
        {
            if (locked) session.Gate.Release();
            lock (job.Sync) { job.CurrentPath = null; job.State = "finalizing"; }
            try
            {
                // Completed/failed files remain available on cancellation and are never silently deleted.
                var manifest = PathPolicy.OutputFile(job.OutputDirectory, "manifest.json");
                await System.IO.File.WriteAllTextAsync(manifest, JsonConvert.SerializeObject(new
                {
                    status = job.Snapshot(finalState), results = job.Results,
                    files = System.IO.Directory.EnumerateFiles(job.OutputDirectory, "*", SearchOption.AllDirectories)
                        .Where(p => p != manifest).Select(p => new { path = Path.GetRelativePath(job.OutputDirectory, p).Replace('\\', '/'), bytes = new FileInfo(p).Length }).ToArray(),
                    note = "Cancellation and failures can leave partial files. Inspect per-asset success before using the outputs."
                }, Formatting.Indented));
            }
            catch (Exception ex) { lock (job.Sync) { job.Error = "Manifest could not be written: " + ErrorMessage(ex); finalState = "failed"; } }
            lock (job.Sync) job.State = finalState;
        }
    }

    private async Task<ExportItem> ExportOne(GameSession session, ExportRequest request, string path, string root, CancellationToken ct)
    {
        var file = File(session, path); CheckSize(file);
        PathPolicy.ValidateRelative(file.Path);
        if (request.Mode == "raw")
        {
            foreach (var payload in session.Files.Where(f => f.IsUePackagePayload && f.PathWithoutExtension == file.PathWithoutExtension)) CheckSize(payload);
            var data = file.IsUePackage ? session.Provider.SavePackage(file) : new Dictionary<string, byte[]> { [file.Path] = file.Read() };
            var written = new List<string>();
            foreach (var (name, bytes) in data)
            {
                ct.ThrowIfCancellationRequested();
                var output = PathPolicy.OutputFile(root, "raw/" + name);
                await System.IO.File.WriteAllBytesAsync(output, bytes, ct); written.Add(output);
            }
            return new(path, written.Count > 0, written.ToArray(), written.Count == 0 ? "Parser returned no raw files." : null);
        }
        var package = LoadPackage(session, path);
        var objects = request.ObjectName is null ? package.GetExports() : [package.GetExport(request.ObjectName)];
        if (request.Mode == "properties")
        {
            var output = PathPolicy.OutputFile(root, "properties/" + file.PathWithoutExtension + ".json");
            using var stream = new StreamWriter(output, false, new UTF8Encoding(false));
            using var writer = new JsonTextWriter(stream) { Formatting = Formatting.Indented };
            var serializer = JsonSerializer.CreateDefault();
            writer.WriteStartArray();
            foreach (var obj in objects) { ct.ThrowIfCancellationRequested(); serializer.Serialize(writer, obj); }
            writer.WriteEndArray();
            return new(path, true, [output]);
        }
        if (request.Mode == "audio")
        {
            var written = new List<string>();
            foreach (var obj in objects)
            {
                ct.ThrowIfCancellationRequested();
                obj.Decode(request.DecompressAudio, out var format, out var bytes);
                if (bytes is not { Length: > 0 }) continue;
                var output = PathPolicy.OutputFile(root, "audio/" + file.PathWithoutExtension + "/" + obj.Name + "." + format.ToLowerInvariant());
                await System.IO.File.WriteAllBytesAsync(output, bytes, ct); written.Add(output);
            }
            if (written.Count == 0) throw new NotSupportedException("No decodable SoundWave, SoundNodeWave or AkMediaAssetData export. Sound cues and external audio banks may reference separate audio assets; inspect their properties.");
            return new(path, true, written.ToArray());
        }
        var conversionRoot = PathPolicy.OutputFile(root, "converted/.reserved");
        conversionRoot = Path.GetDirectoryName(conversionRoot)!;
        var exports = new ExportSession((args, _) =>
        {
            foreach (var level in args.StreamingLevels) level.IsPersistent = request.IncludeStreamingLevels;
            foreach (var actor in args.Actors) ConfigureSublevels(actor, request.IncludeStreamingLevels);
        })
        {
            MaxDegreeOfParallelism = 1,
            OutputPathValidator = candidate => ValidateConversionOutput(conversionRoot, candidate)
        };
        var skipped = new HashSet<string>();
        foreach (var obj in objects)
        {
            ct.ThrowIfCancellationRequested();
            if (obj is UAnimSequence { CompressedDataStructure: null, RawAnimationData: null or { Length: 0 } })
                throw new NotSupportedException("Animation compression data is unavailable. Mount its compression-settings/codec dependencies and use the matching game profile. Raw and properties exports remain available.");
            try { exports.Add(obj); }
            catch (NotSupportedException) { skipped.Add(obj.ExportType); }
        }
        if (!exports.HasQueuedItems) throw new NotSupportedException("No supported converted exports. Use properties/raw/audio mode for this asset. Types: " + string.Join(", ", skipped));
        var result = await exports.RunAsync(conversionRoot, BuildExportOptions(request, session), ct: ct);
        var errors = result.Where(r => !r.Success).Select(r => r.ObjectPath + ": " + ErrorMessage(r.Error ?? new Exception("Export failed"))).ToList();
        var outputs = result.SelectMany(r => r.DiskFilePaths ?? []).ToArray();
        return new(path, result.Count > 0 && errors.Count == 0, outputs,
            errors.Count > 0 ? string.Join("; ", errors) : skipped.Count > 0 ? "Skipped unsupported export types: " + string.Join(", ", skipped) : null);
    }

    internal static string ValidateConversionOutput(string root, string candidate)
    {
        var full = Path.GetFullPath(candidate);
        if (!PathPolicy.IsWithin(root, full)) throw new ArgumentException("Converted asset output escapes its export directory.");
        PathPolicy.ValidateRelative(Path.GetRelativePath(root, full));
        PathPolicy.RejectLinks(full);
        return full;
    }

    private void ValidateExport(ExportRequest r)
    {
        if (r.Paths is not { Length: > 0 } || r.Paths.Length > _options.MaxBatchAssets || r.Paths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Supply 1..{_options.MaxBatchAssets} explicit asset paths.");
        if (r.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != r.Paths.Length) throw new ArgumentException("Duplicate export paths are not allowed.");
        r.Mode = r.Mode.ToLowerInvariant();
        if (r.Mode is not ("raw" or "properties" or "converted" or "audio")) throw new ArgumentException("mode must be raw, properties, converted or audio.");
        if (r.ObjectName is not null && r.Paths.Length != 1) throw new ArgumentException("objectName is only supported for a single package.");
        if (r.Mode == "raw" && r.ObjectName is not null) throw new ArgumentException("Raw export works at package/file level; omit objectName.");
        if (r.TextureQuality is < 1 or > 100) throw new ArgumentException("textureQuality must be 1..100.");
        ParseEnum<EMeshFormat>(r.MeshFormat); ParseEnum<EMeshQuality>(r.MeshQuality); ParseEnum<ENaniteMeshFormat>(r.NaniteMeshFormat);
        ParseEnum<ETextureFormat>(r.TextureFormat); ParseEnum<EMaterialDepth>(r.MaterialDepth); ParseEnum<ESocketFormat>(r.SocketFormat); ParseEnum<EFileCompressionFormat>(r.CompressionFormat);
    }
    private static ExportOptions BuildExportOptions(ExportRequest r, GameSession s) => new(
        meshFormat: ParseEnum<EMeshFormat>(r.MeshFormat), naniteMeshFormat: ParseEnum<ENaniteMeshFormat>(r.NaniteMeshFormat),
        meshQuality: ParseEnum<EMeshQuality>(r.MeshQuality), texturePlatform: s.Provider.Versions.Platform,
        textureFormat: ParseEnum<ETextureFormat>(r.TextureFormat), textureQuality: r.TextureQuality,
        exportHdrTexturesAsHdr: r.ExportHdrTexturesAsHdr, exportAllTextureMips: r.ExportAllTextureMips,
        materialDepth: ParseEnum<EMaterialDepth>(r.MaterialDepth), exportMaterials: r.ExportMaterials,
        exportMorphTargets: r.ExportMorphTargets, socketFormat: ParseEnum<ESocketFormat>(r.SocketFormat), compressionFormat: ParseEnum<EFileCompressionFormat>(r.CompressionFormat));
    private ExportJob Job(string id) => _jobs.TryGetValue(id, out var job) ? job : throw new KeyNotFoundException("Unknown or expired jobId. Use fmodel_list_jobs.");
    private static bool IsTerminal(ExportJob job) { lock (job.Sync) return job.Terminal; }
    private static void ConfigureSublevels(ActorDto actor, bool include)
    {
        foreach (var level in actor.StreamingLevels ?? []) level.IsPersistent = include;
        ConfigureComponentSublevels(actor.RootComponent, include);
    }
    private static void ConfigureComponentSublevels(SceneComponentDto? component, bool include)
    {
        if (component is null) return;
        foreach (var child in component.Children) ConfigureComponentSublevels(child, include);
        foreach (var actor in component.AttachedActors) ConfigureSublevels(actor, include);
    }
    internal static string ErrorMessage(Exception ex)
    {
        // Do not serialize exceptions, stack traces or provider state (which can contain AES keys).
        var message = System.Text.RegularExpressions.Regex.Replace(ex.GetBaseException().Message, @"(?:0x)?[0-9a-fA-F]{64}", "[redacted key/hash]");
        return message.Length <= 1200 ? message : message[..1200];
    }
}
