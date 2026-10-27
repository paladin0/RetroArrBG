using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RetroArr.Core.Data;
using RetroArr.Core.Games;

namespace RetroArr.Api.V3.Collections
{
    /// <summary>
    /// V17 phase 1: explicitly reviewed imports of already-extracted game collections.
    /// No remote downloads, archive extraction, overwrites or deletes are performed.
    /// </summary>
    [ApiController]
    [Route("api/v3/collection-import")]
    public sealed class CollectionImportController : ControllerBase
    {
        private readonly RetroArrDbContext _db;
        private const string StagingRoot = "/downloads";
        private const string LibraryRoot = "/media";
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".z64", ".n64", ".v64", ".nes", ".sfc", ".smc", ".gba", ".gb", ".gbc",
            ".nds", ".3ds", ".cia", ".iso", ".gcm", ".rvz", ".wbfs", ".cso", ".chd",
            ".cue", ".bin", ".cdi", ".gdi", ".nsp", ".xci", ".pbp", ".nrg"
        };
        public CollectionImportController(RetroArrDbContext db) => _db = db;

        public class PreviewRequest
        {
            public string CollectionFolder { get; set; } = ""; // path relative to /downloads
            public int PlatformId { get; set; }
        }
        public sealed class ImportRequest : PreviewRequest
        {
            public List<string> SelectedFiles { get; set; } = new();
            public string ReviewToken { get; set; } = "";
        }
        private sealed class Candidate
        {
            public string File { get; set; } = "";
            public string Title { get; set; } = "";
            public string Status { get; set; } = "";
            public int? ExistingGameId { get; set; }
            public bool DefaultSelected { get; set; }
            public long Size { get; set; }
        }
        private sealed class Inventory
        {
            public string Folder { get; set; } = "";
            public Platform Platform { get; set; } = null!;
            public List<Candidate> Candidates { get; set; } = new();
            public string Token { get; set; } = "";
        }
        private static string NameKey(string title) => Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "");
        private static string GuessTitle(string filename)
        {
            var name = Path.GetFileNameWithoutExtension(filename);
            name = Regex.Replace(name, "\\s*[\\[(](usa|europe|japan|world|en|fr|de|rev[^)]*|v[0-9.]+)[\\])]", "", RegexOptions.IgnoreCase);
            return Regex.Replace(name.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim();
        }
        private static string SafeFolder(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains("..") || name.Contains('\\') || name.StartsWith('.'))
                throw new ArgumentException("CollectionFolder must be a simple folder name beneath /downloads");
            if (name.Contains('/') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("CollectionFolder must not include path separators");
            return name;
        }
        private static string CheckRealFile(string folder, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
                throw new ArgumentException("Invalid relative file path");
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("File escapes collection folder");
            var cursor = folder;
            if ((System.IO.File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Symlinked collection folders are not allowed");
            foreach (var segment in relative.Split('/'))
            {
                if (segment is "" or "." or "..") throw new ArgumentException("Invalid path component");
                cursor = Path.Combine(cursor, segment);
                if ((System.IO.File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Symlinks are not allowed");
            }
            return path;
        }
        private async Task<Inventory> Build(PreviewRequest request)
        {
            var platform = await _db.Platforms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PlatformId)
                ?? throw new ArgumentException("Unknown platform");
            var folder = Path.Combine(StagingRoot, SafeFolder(request.CollectionFolder));
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Collection staging folder not found");
            if ((System.IO.File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Symlinked folder not allowed");
            var games = await _db.Games.AsNoTracking().Where(g => g.PlatformId == request.PlatformId)
                .Select(g => new { g.Id, g.Title, g.AlternativeTitle }).ToListAsync();
            var lookup = games.SelectMany(g => new[] { g.Title, g.AlternativeTitle }.Where(t => !string.IsNullOrEmpty(t))
                .Select(t => new { Key = NameKey(t!), g.Id }))
                .GroupBy(g => g.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().ToArray());
            var inventory = new Inventory { Folder = folder, Platform = platform };
            var paths = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(5001).ToList();
            if (paths.Count > 5000) throw new ArgumentException("Collection exceeds 5000 files; split into smaller batches");
            foreach (var path in paths)
            {
                if (!Allowed.Contains(Path.GetExtension(path))) continue;
                var rel = Path.GetRelativePath(folder, path).Replace('\\', '/');
                var checkedPath = CheckRealFile(folder, rel);
                var title = GuessTitle(Path.GetFileName(path));
                var matches = lookup.TryGetValue(NameKey(title), out var ids) ? ids : Array.Empty<int>();
                var status = matches.Length > 1 ? "ambiguous" : matches.Length == 1 ? "catalogued" : "new";
                inventory.Candidates.Add(new Candidate
                {
                    File = rel, Title = title, Status = status,
                    ExistingGameId = matches.Length == 1 ? matches[0] : null,
                    Size = new FileInfo(checkedPath).Length,
                    DefaultSelected = matches.Length == 0 && !string.IsNullOrWhiteSpace(title)
                });
            }
            var stamp = string.Join("\n", inventory.Candidates.OrderBy(x => x.File, StringComparer.Ordinal).Select(x =>
                $"{x.File}|{x.Size}|{System.IO.File.GetLastWriteTimeUtc(Path.Combine(folder, x.File)).Ticks}"));
            inventory.Token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.PlatformId}|{request.CollectionFolder}|{stamp}")));
            return inventory;
        }
        [HttpPost("preview")]
        public async Task<IActionResult> Preview([FromBody] PreviewRequest request)
        {
            try
            {
                var inv = await Build(request);
                return Ok(new { request.CollectionFolder, request.PlatformId, platform = inv.Platform.Name,
                    reviewToken = inv.Token, items = inv.Candidates,
                    total = inv.Candidates.Count, newGames = inv.Candidates.Count(c => c.Status == "new"),
                    catalogued = inv.Candidates.Count(c => c.Status == "catalogued"),
                    ambiguous = inv.Candidates.Count(c => c.Status == "ambiguous") });
            }
            catch (Exception e) when (e is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            { return BadRequest(new { error = e.Message }); }
        }
        [HttpPost("import")]
        public async Task<IActionResult> Import([FromBody] ImportRequest request)
        {
            try
            {
                var inv = await Build(request);
                if (!string.Equals(inv.Token, request.ReviewToken, StringComparison.Ordinal))
                    return Conflict(new { error = "Collection contents changed since preview; review again" });
                var requested = request.SelectedFiles.Distinct(StringComparer.Ordinal).ToArray();
                if (requested.Length > 5000) return BadRequest(new { error = "Too many files" });
                var byFile = inv.Candidates.ToDictionary(c => c.File, StringComparer.Ordinal);
                if (requested.Any(f => !byFile.ContainsKey(f))) return BadRequest(new { error = "Invalid file selection" });
                var results = new List<object>();
                foreach (var rel in requested)
                {
                    var candidate = byFile[rel];
                    if (candidate.Status == "ambiguous") { results.Add(new { file = rel, status = "needs_manual_match" }); continue; }
                    var original = CheckRealFile(inv.Folder, rel);
                    if (candidate.ExistingGameId.HasValue)
                    {
                        var current = await _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == candidate.ExistingGameId.Value);
                        var linkedFiles = await _db.GameFiles.AsNoTracking().AnyAsync(g => g.GameId == candidate.ExistingGameId.Value);
                        if (linkedFiles || (current != null && !string.IsNullOrWhiteSpace(current.Path) && System.IO.File.Exists(current.Path)))
                        {
                            results.Add(new { file = rel, status = "existing_game_has_files" });
                            continue;
                        }
                    }
                    var platformFolder = Regex.Replace(inv.Platform.FolderName, @"[^a-zA-Z0-9_-]", "");
                    if (string.IsNullOrWhiteSpace(platformFolder)) return BadRequest(new { error = "Unsafe platform destination" });
                    var targetDir = Path.Combine(LibraryRoot, platformFolder);
                    if (Directory.Exists(targetDir) && (System.IO.File.GetAttributes(targetDir) & FileAttributes.ReparsePoint) != 0)
                        return BadRequest(new { error = "Symlinked library directories are not allowed" });
                    Directory.CreateDirectory(targetDir);
                    var target = Path.Combine(targetDir, Path.GetFileName(original));
                    if (System.IO.File.Exists(target)) { results.Add(new { file = rel, status = "already_exists" }); continue; }
                    // The library is never overwritten. Use CreateNew and no deletion of the staging source.
                    try
                    {
                        await using (var source = System.IO.File.OpenRead(original))
                        await using (var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            await source.CopyToAsync(dest);
                    }
                    catch (IOException e) { results.Add(new { file = rel, status = "copy_error", reason = e.Message }); continue; }
                    var game = candidate.ExistingGameId.HasValue
                        ? await _db.Games.FirstOrDefaultAsync(g => g.Id == candidate.ExistingGameId.Value)
                        : null;
                    if (game == null)
                    {
                        game = new Game { Title = candidate.Title, PlatformId = request.PlatformId, Added = DateTime.UtcNow,
                            Monitored = false, Status = GameStatus.Downloaded, MetadataSource = "CollectionImport",
                            NeedsMetadataReview = true, MetadataReviewReason = "Imported from collection; confirm metadata" };
                        _db.Games.Add(game);
                    }
                    game.Path = target;
                    game.Status = GameStatus.Downloaded;
                    game.SizeOnDisk = new FileInfo(target).Length;
                    await _db.SaveChangesAsync();
                    if (!await _db.GameFiles.AnyAsync(f => f.GameId == game.Id && f.RelativePath == target))
                    {
                        _db.GameFiles.Add(new GameFile { GameId = game.Id, RelativePath = target,
                            Size = game.SizeOnDisk ?? 0, DateAdded = DateTime.UtcNow, FileType = "Main" });
                        await _db.SaveChangesAsync();
                    }
                    results.Add(new { file = rel, status = "imported", gameId = game.Id });
                }
                return Ok(new { results, message = "Selected files processed. Staged originals retained." });
            }
            catch (Exception e) when (e is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            { return BadRequest(new { error = e.Message }); }
        }
    }
}


