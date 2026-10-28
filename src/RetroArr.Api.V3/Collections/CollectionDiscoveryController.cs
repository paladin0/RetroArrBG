using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RetroArr.Core.Configuration;
using RetroArr.Core.Data;
using RetroArr.Core.Prowlarr;

namespace RetroArr.Api.V3.Collections
{
    /// <summary>
    /// V20: read-only collection discovery. No NZB grab, extraction, or filesystem mutation.
    /// Results deliberately omit credential-bearing download URLs. Candidate identifiers
    /// are informational and MUST NOT be trusted as authorization for a future grab.
    /// </summary>
    [ApiController]
    [Route("api/v3/collection-discovery")]
    public sealed class CollectionDiscoveryController : ControllerBase
    {
        private readonly RetroArrDbContext _db;
        private readonly ConfigurationService _config;
        public CollectionDiscoveryController(RetroArrDbContext db, ConfigurationService config)
        {
            _db = db;
            _config = config;
        }

        private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["n64"] = new[] { "Nintendo64", "Nintendo 64", "N64" },
            ["gamecube"] = new[] { "Nintendo Gamecube", "NGC", "GameCube" },
            ["snes"] = new[] { "SNES", "Super Nintendo", "Super Famicom" },
            ["nes"] = new[] { "NES", "Nintendo Entertainment System", "Famicom" },
            ["psx"] = new[] { "PlayStation", "PSX", "PS1" },
            ["ps2"] = new[] { "PlayStation 2", "PS2" },
            ["ps3"] = new[] { "PlayStation 3", "PS3" },
            ["psp"] = new[] { "PSP", "PlayStation Portable" },
            ["gba"] = new[] { "GBA", "Game Boy Advance" },
            ["gb"] = new[] { "Game Boy", "GB" },
            ["gbc"] = new[] { "Game Boy Color", "GBC" },
            ["nds"] = new[] { "Nintendo DS", "NDS" },
            ["3ds"] = new[] { "Nintendo 3DS", "3DS" },
            ["wii"] = new[] { "Nintendo Wii", "Wii" },
            ["wiiu"] = new[] { "Wii U", "WiiU" },
            ["dreamcast"] = new[] { "Dreamcast", "Sega Dreamcast" },
            ["saturn"] = new[] { "Sega Saturn", "Saturn" },
            ["megadrive"] = new[] { "Mega Drive", "Genesis" },
            ["mastersystem"] = new[] { "Master System", "SMS" },
        };

        private static string Simplify(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");
        private static bool GameCategory(SearchResult r) => r.Categories != null && r.Categories.Any(c =>
        {
            var id = c.Id % 100000;
            return (id >= 1000 && id < 2000) || (id >= 4000 && id < 5000);
        }) && !r.Categories.Any(c =>
        {
            var id = c.Id % 100000;
            return (id >= 2000 && id < 4000) || (id >= 5000 && id < 8000);
        });

        private static bool CollectionTitle(string title) => Regex.IsMatch(title,
            @"(?:complete[._\s-]*(?:usa[._\s-]*)?(?:rom[._\s-]*)?(?:set|collection)|no[._\s-]*intro|redump|good[._\s-]*(?:roms|set)|rom[._\s-]*set|full[._\s-]*(?:rom[._\s-]*)?(?:set|collection))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [HttpGet("candidates")]
        public async Task<IActionResult> Candidates([FromQuery] int platformId, [FromQuery] int maxQueries = 3)
        {
            var platform = await _db.Platforms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == platformId);
            if (platform == null) return BadRequest(new { error = "Unknown platform" });
            var settings = _config.LoadProwlarrSettings();
            if (!settings.Enabled || !settings.IsConfigured)
                return BadRequest(new { error = "Prowlarr is not enabled/configured" });
            // Bound request volume even for clients that supply enormous maxQueries.
            maxQueries = Math.Clamp(maxQueries, 1, 4);
            var key = Simplify(platform.FolderName);
            if (!Aliases.TryGetValue(key, out var aliases))
            {
                key = Simplify(platform.Slug);
                if (!Aliases.TryGetValue(key, out aliases)) aliases = new[] { platform.Name };
            }
            var queries = aliases.Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(maxQueries).ToArray();
            var client = new ProwlarrClient(settings.Url, settings.ApiKey);
            var releases = new List<SearchResult>();
            var errors = new List<string>();
            foreach (var q in queries)
            {
                try { releases.AddRange(await client.SearchAsync(q)); }
                catch (Exception ex) { errors.Add($"{q}: {ex.GetType().Name}"); }
            }
            var matches = releases.Where(GameCategory).Where(x => CollectionTitle(x.Title))
                .Where(x => aliases.Any(alias =>
                    Simplify(x.Title).Contains(Simplify(alias), StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => new { x.Title, x.Size, x.IndexerName })
                .Select(g => g.First())
                .OrderBy(x => x.Size)
                .Take(100)
                .Select(x => new
                {
                    x.Title,
                    x.Size,
                    x.IndexerName,
                    x.Protocol,
                    categoryIds = x.Categories.Select(c => c.Id).ToArray(),
                    requiresReview = true,
                    action = "review_only"
                }).ToArray();
            return Ok(new
            {
                platformId, platform = platform.Name, queries,
                searched = releases.Count, candidates = matches,
                count = matches.Length, errors,
                automaticDispatchEnabled = false,
                notice = "Discovery only. Review, download and extraction are not enabled by this endpoint."
            });
        }
    }
}
