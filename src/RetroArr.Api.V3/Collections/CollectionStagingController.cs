using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace RetroArr.Api.V3.Collections
{
    /// <summary>
    /// V20 staging: explicit ZIP extraction only, no automatic downloads or imports.
    /// The existing collection-import/preview endpoint provides the review inventory.
    /// </summary>
    [ApiController]
    [Route("api/v3/collection-staging")]
    public sealed class CollectionStagingController : ControllerBase
    {
        private const string Root = "/downloads";
        private const long MaxExtractedBytes = 8L * 1024 * 1024 * 1024;
        private const int MaxEntries = 5000;
        private static readonly string[] AllowedExtensions = { ".z64", ".n64", ".v64", ".nes", ".sfc", ".smc", ".gba", ".gb", ".gbc", ".nds", ".3ds", ".cia", ".iso", ".gcm", ".rvz", ".wbfs", ".cso", ".chd", ".cue", ".bin", ".cdi", ".gdi", ".nsp", ".xci", ".pbp", ".nrg" };

        public sealed class StageRequest
        {
            public string ArchiveName { get; set; } = "";
        }

        private static bool SafeName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) &&
            !name.Contains("..", StringComparison.Ordinal) && !name.StartsWith(".", StringComparison.Ordinal) &&
            !name.Contains('/') && !name.Contains('\\');

        [HttpPost("zip")]
        public IActionResult StageZip([FromBody] StageRequest request)
        {
            if (!SafeName(request.ArchiveName) || !request.ArchiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Provide a single ZIP filename directly under /downloads" });
            var archiveFile = Path.Combine(Root, request.ArchiveName);
            if (!System.IO.File.Exists(archiveFile) ||
                (System.IO.File.GetAttributes(archiveFile) & FileAttributes.ReparsePoint) != 0)
                return BadRequest(new { error = "ZIP not found or symlinked" });
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ArchiveName))).Substring(0, 16).ToLowerInvariant();
            var targetName = "retroarr-review-" + digest;
            var target = Path.Combine(Root, targetName);
            if (Directory.Exists(target))
                return Conflict(new { error = "Existing staged folder, review or remove it manually before retrying", collectionFolder = targetName });
            try
            {
                using var archive = ZipFile.OpenRead(archiveFile);
                if (archive.Entries.Count > MaxEntries) return BadRequest(new { error = "Collection exceeds 5000 entries" });
                long total = 0;
                var valid = 0;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var segments = entry.FullName.Replace('\\', '/').Split('/');
                    if (segments.Any(x => x is "" or "." or "..") || entry.FullName.StartsWith('/') || Path.IsPathRooted(entry.FullName))
                        return BadRequest(new { error = "Unsafe archive entry path" });
                    // Unix symlinks have file-type bits 0120000 in the upper mode word.
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType == 0xA000) return BadRequest(new { error = "ZIP symlink entries are not allowed" });
                    if (!AllowedExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase)) continue;
                    if (entry.Length <= 0 || entry.Length > MaxExtractedBytes || total > MaxExtractedBytes - entry.Length)
                        return BadRequest(new { error = "Empty or oversized ROM entry / collection exceeds 8 GiB" });
                    total += entry.Length;
                    valid++;
                }
                if (valid == 0) return BadRequest(new { error = "No supported nonempty game files in ZIP" });
                Directory.CreateDirectory(target);
                try
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name) || !AllowedExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase)) continue;
                        var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
                        if (!dest.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            throw new InvalidDataException("ZIP entry outside staging folder");
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using var src = entry.Open();
                        using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        var buffer = new byte[65536];
                        long written = 0;
                        int n;
                        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            written += n;
                            if (written > entry.Length) throw new InvalidDataException("Archive length mismatch");
                            dst.Write(buffer, 0, n);
                        }
                        if (written != entry.Length) throw new InvalidDataException("Incomplete archive entry");
                    }
                }
                catch
                {
                    Directory.Delete(target, recursive: true);
                    throw;
                }
                return Ok(new { collectionFolder = targetName, fileCount = valid, extractedBytes = total,
                    next = "/api/v3/collection-import/preview", notice = "Staged only. No game files imported." });
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("review")]
        public ContentResult ReviewPage() => Content(ReviewHtml, "text/html; charset=utf-8");

        private const string ReviewHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>RetroArr Collection Review</title>
<style>body{font:15px system-ui;background:#161b22;color:#edf2f7;max-width:950px;margin:36px auto;padding:16px}input,button{padding:9px;margin:5px;background:#283241;color:white;border:1px solid #59677d;border-radius:5px}button{cursor:pointer}table{border-collapse:collapse;width:100%}td,th{padding:9px;border-bottom:1px solid #455166;text-align:left}small{color:#aebbd1}.warn{color:#ffcf83}pre{white-space:pre-wrap}</style></head><body>
<h2>RetroArr Collection Review</h2><p class="warn">Explicit review only. ZIP staging and preview do not download or import games.</p>
<label>ZIP filename in /downloads <input id="archive" placeholder="MyCollection.zip"></label><button id="stage">Stage ZIP</button><hr>
<label>Staged folder <input id="folder" placeholder="retroarr-review-..."></label><label>RetroArr Platform ID <input id="platform" type="number" value="42"></label><button id="preview">Preview collection</button>
<p id="summary"></p><table><thead><tr><th>Keep</th><th>Game</th><th>Match status</th><th>Size</th></tr></thead><tbody id="items"></tbody></table>
<p class="warn">Import is deliberately not enabled in this screen until file matching and database changes pass integration testing.</p><pre id="error"></pre>
<script>
const $=id=>document.getElementById(id); const jsonPost=async(url,data)=>{const r=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});const j=await r.json();if(!r.ok)throw Error(j.error||('HTTP '+r.status));return j};
$('stage').onclick=async()=>{try{$('error').textContent='';const x=await jsonPost('/api/v3/collection-staging/zip',{archiveName:$('archive').value});$('folder').value=x.collectionFolder;$('summary').textContent='Staged '+x.fileCount+' files. No import performed.'}catch(e){$('error').textContent=e.message}};
$('preview').onclick=async()=>{try{$('error').textContent='';const x=await jsonPost('/api/v3/collection-import/preview',{collectionFolder:$('folder').value,platformId:Number($('platform').value)});$('summary').textContent=x.platform+': '+x.total+' files; '+x.newGames+' new, '+x.catalogued+' catalogued, '+x.ambiguous+' ambiguous';$('items').replaceChildren();for(const item of x.items){const tr=document.createElement('tr');const td=document.createElement('td'),cb=document.createElement('input');cb.type='checkbox';cb.checked=!!item.defaultSelected;cb.disabled=true;td.append(cb);tr.append(td);for(const value of [item.title,item.status,String(item.size)]){const cell=document.createElement('td');cell.textContent=value;tr.append(cell)}$('items').append(tr)}}catch(e){$('error').textContent=e.message}};
</script></body></html>
""";
    }
}
