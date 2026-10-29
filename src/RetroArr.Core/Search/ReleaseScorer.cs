using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using RetroArr.Core.Configuration;
using RetroArr.Core.Games;
using RetroArr.Core.Prowlarr;

namespace RetroArr.Core.Search
{
    public enum ReleaseDecision
    {
        Reject,        // Hard reject, never auto-download; shown with its reason on request
        Hide,          // Below review threshold, hide by default
        Review,        // Needs human review, surface in manual list
        AutoDownload   // Above auto threshold, safe to download unattended
    }

    public sealed class ScoredRelease
    {
        public SearchResult Release { get; set; } = null!;
        public int Score { get; set; }
        public ReleaseDecision Decision { get; set; }
        public string Reason { get; set; } = string.Empty;
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        public List<string> Signals { get; set; } = new();
    }

    // Per-game release ranking. Pure function over (release, game, settings).
    // Reuses TitleCleanerService for fuzzy match + metadata extraction so the
    // scanner and the scorer agree on what "region X" means.
    public sealed class ReleaseScorer
    {
        public ScoredRelease Score(SearchResult release, Game game, MonitorSettings settings, Func<SearchResult, string?>? blockedReason = null)
        {
            var scored = new ScoredRelease { Release = release };

            // 1. Hard rejects
            if (release == null || string.IsNullOrWhiteSpace(release.Title))
            {
                return Reject(scored, "empty release title");
            }

            if (blockedReason?.Invoke(release) is { } blocked)
            {
                return Reject(scored, $"blacklisted: {blocked}");
            }

            // Platform: if the detector identified a platform and it doesn't
            // match the game's platform folder, reject. If detection failed
            // (no folder or "unknown"), allow through with a signal so the
            // user knows to double-check, but never auto-download it.
            var gamePlatformFolder = game.Platform?.FolderName?.Trim();
            var confirmedPlatform = false;
            var platformUnknown = string.IsNullOrEmpty(release.PlatformFolder)
                || string.Equals(release.PlatformFolder, "unknown", StringComparison.OrdinalIgnoreCase);
            if (!platformUnknown && !string.IsNullOrEmpty(gamePlatformFolder))
            {
                if (!string.Equals(release.PlatformFolder, gamePlatformFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return Reject(scored, $"platform mismatch: release={release.PlatformFolder}, game={gamePlatformFolder}");
                }
                confirmedPlatform = true;
                scored.Signals.Add($"platform match ({release.PlatformFolder})");
            }
            else if (platformUnknown)
            {
                scored.Signals.Add("platform unknown - manual review recommended");
            }

            // Seeders: torrents with no peers are dead, don't queue them.
            if (string.Equals(release.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
            {
                if (release.EffectiveSeeders < settings.MinSeedersTorrent)
                {
                    return Reject(scored, $"insufficient seeders ({release.EffectiveSeeders} < {settings.MinSeedersTorrent})");
                }
            }

            // Title similarity: anchor on game title plus alternative title.
            // ComputeSimilarity is forgiving enough to handle the raw release
            // title; both sides get NormalizeDiacritics-ish treatment inside.
            // Scene/No-Intro naming often prepends the console and appends region,
            // dump source and release group. Compute a second similarity on the
            // stripped title, but never bypass the existing category/platform gates.
            var cleanedTitle = SceneGameTitle(release.Title, game);
            var simMain = Math.Max(
                TitleCleanerService.ComputeSimilarity(release.Title, game.Title ?? string.Empty),
                TitleCleanerService.ComputeSimilarity(cleanedTitle, game.Title ?? string.Empty));
            var simAlt = !string.IsNullOrEmpty(game.AlternativeTitle)
                ? Math.Max(TitleCleanerService.ComputeSimilarity(release.Title, game.AlternativeTitle),
                    TitleCleanerService.ComputeSimilarity(cleanedTitle, game.AlternativeTitle))
                : 0.0;
            var titleSim = Math.Max(simMain, simAlt);
            var titleSimPercent = (int)Math.Round(titleSim * 100);

            if (titleSimPercent < settings.MinTitleSimilarityPercent)
            {
                return Reject(scored, $"title similarity {titleSimPercent}% below floor {settings.MinTitleSimilarityPercent}%");
            }

            // Age limit
            if (settings.MaxReleaseAgeDays > 0 && release.PublishDate != default)
            {
                var age = (DateTime.UtcNow - release.PublishDate).TotalDays;
                if (age > settings.MaxReleaseAgeDays)
                {
                    return Reject(scored, $"release older than {settings.MaxReleaseAgeDays}d");
                }
            }

            // 2. Base score from title similarity (0-40)
            var score = (int)Math.Round(titleSim * 40);
            scored.Signals.Add($"title similarity {titleSimPercent}% (+{score})");

            // A confirmed platform is corroborating evidence, but not enough to
            // approve a download by itself. No bonus when detection is unknown.
            if (confirmedPlatform)
            {
                const int platformMatchBonus = 10;
                score += platformMatchBonus;
                scored.Signals.Add($"confirmed platform (+{platformMatchBonus})");
            }

            // 3. Source / release group
            var trustedSource = false;
            foreach (var src in settings.VerifiedSources)
            {
                if (!string.IsNullOrWhiteSpace(src) && release.Title.IndexOf(src, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += settings.VerifiedSourceBonus;
                    scored.Signals.Add($"verified source '{src}' (+{settings.VerifiedSourceBonus})");
                    trustedSource = true;
                    break;
                }
            }
            if (!trustedSource)
            {
                foreach (var grp in settings.TrustedReleaseGroups)
                {
                    if (!string.IsNullOrWhiteSpace(grp) && release.Title.IndexOf(grp, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var bonus = settings.VerifiedSourceBonus / 2; // half of verified-source bonus
                        score += bonus;
                        scored.Signals.Add($"trusted group '{grp}' (+{bonus})");
                        trustedSource = true;
                        break;
                    }
                }
            }
            // Older console dumps typically have no FitGirl/CODEX-style release group.
            // Do not penalise an otherwise exact, platform-confirmed archival NZB simply
            // because the uploader is unnamed. This is not a trust endorsement: the
            // explicit strong-unverified-NZB opt-in and platform/format safeguards below
            // still govern automatic downloading.
            var archivalConsoleMatch = confirmedPlatform
                && !IsPcPlatform(game.Platform)
                && titleSimPercent >= 95
                && (string.Equals(release.Protocol, "nzb", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(release.Protocol, "usenet", StringComparison.OrdinalIgnoreCase));
            if (!trustedSource && !archivalConsoleMatch)
            {
                score -= settings.UnknownUploaderPenalty;
                scored.Signals.Add($"unknown uploader (-{settings.UnknownUploaderPenalty})");
            }
            else if (!trustedSource && archivalConsoleMatch)
            {
                scored.Signals.Add("legacy console NZB: no PC-style release-group penalty (source unverified)");
            }

            // Per-game preferred release group bonus. Strongest single
            // signal a user can give us: "I want this game from FitGirl".
            if (!string.IsNullOrWhiteSpace(game.PreferredReleaseGroup)
                && release.Title.IndexOf(game.PreferredReleaseGroup, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += settings.PreferredGroupBonus;
                scored.Signals.Add($"preferred group '{game.PreferredReleaseGroup}' (+{settings.PreferredGroupBonus})");
                trustedSource = true; // satisfy RequireTrustedSourceForAuto
            }

            // 4. Release-name metadata vs game preferences
            var (releaseRegion, releaseLanguages, releaseRevision) = TitleCleanerService.ExtractFilenameMetadata(release.Title);

            var preferredRegion = !string.IsNullOrWhiteSpace(game.Region)
                ? game.Region
                : settings.PreferredRegion;
            if (!string.IsNullOrWhiteSpace(preferredRegion) && !string.IsNullOrWhiteSpace(releaseRegion))
            {
                if (preferredRegion.IndexOf(releaseRegion!, StringComparison.OrdinalIgnoreCase) >= 0
                    || releaseRegion!.IndexOf(preferredRegion, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += settings.RegionMatchBonus;
                    scored.Signals.Add($"region match ({releaseRegion}, +{settings.RegionMatchBonus})");
                }
                else
                {
                    score -= settings.WrongRegionPenalty;
                    scored.Signals.Add($"region mismatch (want {preferredRegion}, got {releaseRegion}, -{settings.WrongRegionPenalty})");
                }
            }

            if (!string.IsNullOrWhiteSpace(game.Languages) && !string.IsNullOrWhiteSpace(releaseLanguages))
            {
                var wanted = game.Languages.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(s => s.Trim().ToLowerInvariant())
                                            .Where(s => s.Length > 0).ToHashSet();
                var got = releaseLanguages!.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(s => s.Trim().ToLowerInvariant())
                                            .Where(s => s.Length > 0).ToHashSet();
                if (wanted.Overlaps(got))
                {
                    score += settings.LanguageMatchBonus;
                    scored.Signals.Add($"language overlap (+{settings.LanguageMatchBonus})");
                }
            }

            if (!string.IsNullOrWhiteSpace(game.Revision) && !string.IsNullOrWhiteSpace(releaseRevision))
            {
                // "Rev A, Beta": one shared tag is a match
                var wantedRevisions = game.Revision.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (wantedRevisions.Overlaps(releaseRevision!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
                {
                    score += settings.RevisionMatchBonus;
                    scored.Signals.Add($"revision match ({releaseRevision}, +{settings.RevisionMatchBonus})");
                }
            }

            // 5. Hack / patch detection
            foreach (var token in settings.HackPatchTokens)
            {
                if (!string.IsNullOrWhiteSpace(token) && release.Title.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score -= settings.HackOrPatchPenalty;
                    scored.Signals.Add($"hack/patch token '{token}' (-{settings.HackOrPatchPenalty})");
                    break;
                }
            }

            // 6. Seeders bonus
            if (string.Equals(release.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
            {
                if (release.EffectiveSeeders >= 20)
                {
                    score += 5;
                    scored.Signals.Add($"healthy seeders ({release.EffectiveSeeders}, +5)");
                }
                else if (release.EffectiveSeeders >= 5)
                {
                    score += 2;
                    scored.Signals.Add($"adequate seeders ({release.EffectiveSeeders}, +2)");
                }
            }

            // 7. File-size sanity (very rough: just penalize wildly oversized)
            // 0 means we have no signal, skip.
            if (release.Size > 0 && game.Platform != null)
            {
                var maxSize = MaxReasonableSize(game.Platform);
                if (maxSize > 0)
                {
                    if (release.Size <= maxSize)
                    {
                        score += settings.SizeInRangeBonus;
                        scored.Signals.Add($"size within platform range (+{settings.SizeInRangeBonus})");
                    }
                    else if (release.Size > maxSize * 3)
                    {
                        score -= settings.SizeOutOfRangePenalty;
                        scored.Signals.Add($"size {Bytes(release.Size)} >> platform typical (-{settings.SizeOutOfRangePenalty})");
                    }
                }
            }

            // Clamp and bucket
            score = Math.Max(0, Math.Min(100, score));
            scored.Score = score;

            // Distinguish a strong title/platform match from uploader trust.
            // Unknown uploaders and Virtual Console packages can be reviewed,
            // but are never eligible for unattended downloads. A Virtual Console
            // package is not guaranteed to be an original console ROM.
            var virtualConsole = System.Text.RegularExpressions.Regex.IsMatch(
                release.Title, @"(?:^|[._\s-])Virtual[._\s-]*Console(?:$|[._\s-])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var highConfidenceMatch = confirmedPlatform && titleSimPercent >= 90;
            if (virtualConsole)
                scored.Signals.Add("Virtual Console format - verify file format manually");

            // V21: Separate strong-match auto-download from source reputation.
            // It is an explicit opt-in for NZB *individual* game releases only.
            // The caller has already enforced game categories and excluded collections.
            // Unknown formats (including Virtual Console), unknown platforms and
            // unknown sizes are never eligible for this fallback.
            var nzbProtocol = string.Equals(release.Protocol, "nzb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(release.Protocol, "usenet", StringComparison.OrdinalIgnoreCase);
            var strongUnverifiedAuto = settings.AutoDownloadStrongUnverifiedNzb
                && !trustedSource
                && !virtualConsole
                && confirmedPlatform
                && titleSimPercent >= Math.Max(95, settings.StrongUnverifiedMinSimilarityPercent)
                && score >= Math.Max(35, settings.StrongUnverifiedMinScore)
                && nzbProtocol
                && release.Size > 0
                && (game.Platform != null && MaxReasonableSize(game.Platform) > 0)
                && (game.Platform != null && release.Size <= MaxReasonableSize(game.Platform))
                && !System.Text.RegularExpressions.Regex.IsMatch(release.Title,
                    @"(?:^|[._\s-])(?:demo|beta|alpha|trainer|crack|update|dlc|repack|virtual[._\s-]*console)(?:$|[._\s-])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (highConfidenceMatch && (virtualConsole || !trustedSource))
            {
                if (strongUnverifiedAuto)
                {
                    scored.Decision = ReleaseDecision.AutoDownload;
                    scored.Reason = "explicitly enabled strong-match NZB auto-download";
                    scored.Signals.Add("strong unverified NZB policy accepted - inspect downloaded format on import");
                }
                else
                {
                    scored.Decision = ReleaseDecision.Review;
                    scored.Reason = virtualConsole
                        ? "Virtual Console format requires manual review"
                        : "strong title/platform match; unverified source requires manual review";
                }
                return scored;
            }

            if (score >= settings.AutoDownloadThreshold)
            {
                if (!trustedSource)
                {
                    scored.Decision = ReleaseDecision.Review;
                    scored.Reason = "high score but no trusted source - manual review";
                }
                else if (platformUnknown)
                {
                    scored.Decision = ReleaseDecision.Review;
                    scored.Reason = "high score but platform unknown - manual review";
                }
                else
                {
                    scored.Decision = ReleaseDecision.AutoDownload;
                    scored.Reason = "above auto-download threshold";
                }
            }
            else if (virtualConsole && confirmedPlatform && titleSimPercent >= settings.MinTitleSimilarityPercent)
            {
                scored.Decision = ReleaseDecision.Review;
                scored.Reason = "Virtual Console format requires manual review";
            }
            else if (score >= settings.ReviewThreshold)
            {
                scored.Decision = ReleaseDecision.Review;
                scored.Reason = "above review threshold";
            }
            else
            {
                scored.Decision = ReleaseDecision.Hide;
                scored.Reason = "below review threshold";
            }

            return scored;
        }

        // Only remove recognizable LEADING platform tokens and TRAILING dump
        // metadata, never words in the middle of a game title. For example:
        // "NGC Nintendo Gamecube Star.Wars-Rogue.Squadron.II-Rogue.Leader.Europe.SlipperyJim"
        // becomes "Star Wars Rogue Squadron II Rogue Leader".
        private static string SceneGameTitle(string input, Game game)
        {
            var title = System.Text.RegularExpressions.Regex.Replace(input,
                @"[._]+", " ").Trim();
            var folder = (game.Platform?.FolderName ?? "").ToLowerInvariant();
            var prefix = folder switch
            {
                "gamecube" => @"(?:NGC\s+)?(?:Nintendo\s+)?(?:Gamecube|NGC)",
                "n64" => @"(?:Nintendo\s*64|N64)",
                "psx" or "ps1" => @"(?:Playstation\s*1?|PS1|PSX)",
                "ps2" => @"(?:Playstation\s*2|PS2)",
                "snes" => @"(?:SNES|Super\s*Nintendo|Super\s*Famicom)",
                "nes" => @"(?:NES|Famicom|Nintendo\s*Entertainment\s*System)",
                "gba" => @"(?:GBA|Game\s*Boy\s*Advance)",
                "nds" => @"(?:NDS|Nintendo\s*DS)",
                "wii" => @"(?:Nintendo\s*)?Wii",
                "dreamcast" => @"(?:Sega\s*)?Dreamcast",
                "ps3" => @"(?:Playstation\s*3|PS3)",
                "xbox360" => @"(?:Xbox[\s-]*360|X360|360)",
                "dos" or "msdos" => @"(?:MS[\s-]*DOS|DOS)",
                _ => null
            };
            if (prefix != null)
                title = System.Text.RegularExpressions.Regex.Replace(title,
                    @"^(?:" + prefix + @")\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Platforms and common archival labels can occur at the END of a scene
            // release, e.g. Armored.Core.For.Answer.XBOX360-Group or
            // Airborne.Ranger.MS-DOS. Only strip an exact platform token belonging
            // to the requested game; never infer a platform match from it here.
            if (prefix != null)
                title = System.Text.RegularExpressions.Regex.Replace(title,
                    @"\s+(?:" + prefix + @")(?:\s*[- ]\s*[A-Za-z0-9]{2,18})?$",
                    "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            title = System.Text.RegularExpressions.Regex.Replace(title,
                @"(?:\s+|\s*[-(]\s*)(?:USA|Europe|Japan|World|EUR|JPN|NTSC[ -]?U|PAL|Rev\s*[A-Z0-9]+)(?:\s*\))?(?:\s+.*)?$",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            title = System.Text.RegularExpressions.Regex.Replace(title,
                @"\s+(?:SlipperyJim|ConsoleX|No[ -]?Intro|Redump)$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return title.Trim();
        }

        private static ScoredRelease Reject(ScoredRelease scored, string reason)
        {
            scored.Score = 0;
            scored.Decision = ReleaseDecision.Reject;
            scored.Reason = reason;
            scored.Signals.Add($"REJECT: {reason}");
            return scored;
        }

        private static bool IsPcPlatform(Platform? platform)
        {
            if (platform == null) return false;
            var key = (platform.Slug ?? platform.FolderName ?? string.Empty).ToLowerInvariant();
            return key is "windows" or "pc" or "linux" or "mac" or "macos";
        }

        // Rough upper bound per platform in bytes. Anything beyond ~3x of this
        // is likely a multi-game pack or corrupted archive.
        private static long MaxReasonableSize(Platform platform)
        {
            var slug = platform.Slug?.ToLowerInvariant() ?? string.Empty;
            return slug switch
            {
                "nes" or "gb" or "gbc" or "gg" or "sms" or "atari2600" => 2L * 1024 * 1024,
                "snes" or "gba" or "megadrive" or "genesis" => 16L * 1024 * 1024,
                "n64" or "gamecube" or "saturn" => 1L * 1024 * 1024 * 1024,
                "psx" or "ps1" => 800L * 1024 * 1024,
                "ps2" or "dreamcast" or "wii" => 9L * 1024 * 1024 * 1024,
                "ps3" or "wiiu" or "xbox360" => 25L * 1024 * 1024 * 1024,
                "ps4" or "ps5" or "xboxone" or "xboxseriesx" or "switch" => 100L * 1024 * 1024 * 1024,
                "windows" or "pc" or "mac" or "linux" => 120L * 1024 * 1024 * 1024,
                _ => 0
            };
        }

        private static string Bytes(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024L * 1024) return $"{b / 1024} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024L * 1024)} MB";
            return $"{b / (1024L * 1024 * 1024)} GB";
        }
    }
}
