﻿using BeatmapExporterCore.Exporters.Lazer.LazerDB;
using BeatmapExporterCore.Exporters.Lazer.LazerDB.Schema;
using BeatmapExporterCore.Filters;
using BeatmapExporterCore.Utilities;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace BeatmapExporterCore.Exporters.Lazer
{
    /// <summary>
    /// Exception used when an mp3 transcode operation encounters any error.
    /// </summary>
    public class TranscodeException : ExporterException
    {
        public TranscodeException(Exception inner) : base(inner.Message) { }
    }

    public class LazerExporter : IBeatmapExporter
    {
        readonly LazerDatabase lazerDb;
        readonly Transcoder transcoder;

        readonly List<Beatmap> allBeatmapDiffs;

        /// <param name="lazerDb">The lazer database, referenced for opening files later.</param>
        /// <param name="beatmapSets">All beatmap sets loaded into memory.</param>
        /// <param name="lazerCollections">If available, all collections into memory.</param>
        public LazerExporter(LazerDatabase lazerDb, List<BeatmapSet> beatmapSets, List<BeatmapCollection> lazerCollections)
        {
            this.lazerDb = lazerDb;

            AllBeatmapSets = beatmapSets
                .Where(set => set.Beatmaps.Count > 0)
                .OrderBy(set => set.OnlineID)
                .ToList();
            SelectedBeatmapSets = AllBeatmapSets;
            TotalBeatmapSetCount = AllBeatmapSets.Count;
            SelectedBeatmapSetCount = TotalBeatmapSetCount;

            allBeatmapDiffs = AllBeatmapSets.SelectMany(s => s.Beatmaps).ToList();

            TotalBeatmapCount = allBeatmapDiffs.Count;
            SelectedBeatmapCount = TotalBeatmapCount;

            Configuration = new ExporterConfiguration("lazerexport");

            var colCount = 0;
            Collections = new();
            foreach (var coll in lazerCollections)
            {
                colCount++;
                var colMaps = allBeatmapDiffs
                    .Where(b => coll.BeatmapMD5Hashes.Contains(b.MD5Hash))
                    .ToList();
                Collections[coll.Name] = new MapCollection(colCount, colMaps);
            }
            CollectionCount = colCount;

            transcoder = new Transcoder();
        }


        /// <summary>
        /// Count of the total beatmap sets discovered.
        /// </summary>
        public int TotalBeatmapSetCount
        {
            get; // Total count does not change
        }

        /// <summary>
        /// Count of the individual beatmap difficulties discovered.
        /// </summary>
        public int TotalBeatmapCount
        {
            get;
        }

        /// <summary>
        /// All beatmap sets, without any filtering.
        /// </summary>
        public List<BeatmapSet> AllBeatmapSets
        {
            get;
        }
        
        /// <summary>
        /// The beatmap sets currently selected, after filters are applied.
        /// </summary>
        public List<BeatmapSet> SelectedBeatmapSets
        {
            get;
            private set;
        }

        /// <summary>
        /// Count of the individual beatmap difficulties currently selected, after filters are applied.
        /// </summary>
        public int SelectedBeatmapCount
        {
            get;
            private set;
        }

        /// <summary>
        /// Count of the beatmap sets currently selected, after filters are applied.
        /// </summary>
        public int SelectedBeatmapSetCount
        {
            get;
            private set;
        }

        /// <summary>
        /// All discovered collections. The dictionary key represents the collection's name as chosen by the user.
        /// </summary>
        public Dictionary<string, MapCollection> Collections
        {
            get;
        }

        /// <summary>
        /// Count of all collections discovered
        /// </summary>
        public int CollectionCount
        {
            get;
        }

        /// <summary>
        /// The (mutable) exporter configuration, containing beatmap filter rules and export settings
        /// </summary>
        public ExporterConfiguration Configuration
        {
            get;
        }

        /// <summary>
        /// If audio file transcoding is available
        /// </summary>
        public bool TranscodeAvailable => transcoder.Available;

        public record struct FilterDetail(int Id, string Description, int DiffCount);

        /// <summary>
        /// Returns a list of 'FilterDetail' containers with information about applied filters
        /// </summary>
        public IEnumerable<FilterDetail> Filters() => Configuration.Filters.Select((filter, i) =>
        {
            int diffCount = allBeatmapDiffs.Count(diff => filter.Includes(diff));
            return new FilterDetail(i + 1, filter.Description, diffCount);
        });

        /// <summary>
        /// Creates the export directory and opens the folder (for Windows platform)
        /// </summary>
        public void SetupExport(bool openDir = true)
        {
            Directory.CreateDirectory(Configuration.ExportPath);
            if (openDir) 
                PlatformUtil.OpenExportDirectory(Configuration.ExportPath);
        }

        /// <summary>
        /// Export a single BeatmapSet, with only the 'selected' diffs 
        /// </summary>
        /// <param name="mapset">The BeatmapSet to export</param>
        /// <param name="filename">The output filename that will be used. Will be set regardless of success of export and should be used for user feedback.</param>
        /// <exception cref="IOException">The BeatmapSet export was unsuccessful and an error should be noted to the user.</exception>
        public void ExportBeatmap(BeatmapSet mapset, out string filename)
        {
            // build set of excluded file hashes 
            // these are difficulties in the original beatmap but not 'selected'
            // when doing file export, we will be iterating each file, and every file will be included except for the undesired difficulty files
            var excludedHashes =
                from map in mapset.Beatmaps
                where !mapset.SelectedBeatmaps.Contains(map)
                select map.Hash;
            var excluded = excludedHashes.ToList();

            Stream? export = null;
            try
            {
                filename = mapset.ArchiveFilename();
                string exportPath = Path.Combine(Configuration.ExportPath, filename);
                export = File.Open(exportPath, FileMode.CreateNew);

                using ZipArchive osz = new(export, ZipArchiveMode.Create, true);
                foreach (var namedFile in mapset.Files)
                {
                    string hash = namedFile.File.Hash;
                    if (excluded.Contains(hash))
                        continue;
                    var entry = osz.CreateEntry(namedFile.Filename, Configuration.CompressionLevel);
                    using var entryStream = entry.Open();
                    // open the actual difficulty/audio/image file from the lazer file store
                    using var file = lazerDb.OpenHashedFile(hash);
                    file?.CopyTo(entryStream);
                }
            }
            finally
            {
                export?.Dispose();
            }
        }
        
        public record struct AudioExportTask(BeatmapSet OriginSet, BeatmapMetadata AudioFile, string? TranscodeFrom, string OutputFilename);

        /// <summary>
        /// Analyze a beatmap set, producing AudioExportTask containers for each unique audio track.
        /// </summary>
        public IEnumerable<AudioExportTask> ExtractAudio(BeatmapSet mapset)
        {
            // perform export of songs as .mp3 files
            // get any beatmap diffs from this set with different audio files
            // typically 1 'audio.mp3' only by convention. could also be multiple across different difficulties
            var uniqueMetadata = mapset
                .SelectedBeatmaps
                .Select(b => b.Metadata)
                .DistinctBy(m => m.AudioFile)
                .ToList();

            int beatmapAudioCount = 0;
            foreach (var metadata in uniqueMetadata)
            {
                // transcode if audio is not in .mp3 format

                // self code added here
                IList<RealmNamedFileUsage> targetFiles = mapset.Files;
                Console.WriteLine(targetFiles[0].File.Hash);
                Console.WriteLine("Path location: ", mapset.Files.ToString());
                // ====================
                string extension = Path.GetExtension(metadata.AudioFile);
                string? transcodeFrom = null;
                if (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    transcodeFrom = extension;
                }

                // produce more meaningful filename than 'audio.mp3' 
                string outputFilename = metadata.OutputAudioFilename(beatmapAudioCount);

                beatmapAudioCount++;
                yield return new AudioExportTask(mapset, metadata, transcodeFrom, outputFilename);
            }
        }

        /// <summary>
        /// Export a single audio file from a beatmap.
        /// If the FFMpeg runtime is available, non-mp3 audio files will have a transcode attempted into mp3 format. 
        /// </summary>
        /// <param name="export">An AudioExportTask container, as produced by <see cref="ExportAudio(AudioExportTask, Action{Exception})"/></param>
        /// <param name="metadataFailure">An optional callback to notify users on metadata assignment failure. As metadata is non-critical, export will always continue.</param>
        /// <exception cref="TranscodeException">Audio transcode is required for this file and has failed.</exception>
        /// <exception cref="Exception">General audio file export has failed.</exception>
        public async Task ExportAudio(AudioExportTask export, Action<Exception>? metadataFailure)
        {            
            var (mapset, metadata, transcodeFrom, outputFilename) = export;
            string outputFile = Path.Combine(Configuration.ExportPath, outputFilename);

            using FileStream? audio = lazerDb.OpenNamedFile(mapset, metadata.AudioFile);
            if (audio is null)
            {
                throw new IOException($"Audio file {metadata.AudioFile} not found in beatmap {mapset.ArchiveFilename()}.");
            }
                
            // Create physical .mp3 file, either through transcoding or simple copying
            if (transcodeFrom != null)
            {
                // transcoder (FFmpeg) not available, skipping. 
                if (!TranscodeAvailable)
                {
                    return;
                }
                try
                {
                    await Task.Run(() => transcoder.TranscodeMP3(audio, outputFile));
                    // transcoder.TranscodeMP3(audio, outputFile);
                }
                catch (Exception e)
                {
                    throw new TranscodeException(e);
                }
            }
            else
            {
                using FileStream output = File.Open(outputFile, FileMode.CreateNew);
                audio.CopyTo(output);
            }

            // set mp3 tags 
            try
            {
                var mp3 = TagLib.File.Create(outputFile);
                if (string.IsNullOrEmpty(mp3.Tag.Title))
                    mp3.Tag.Title = metadata.TitleUnicode;
                if (mp3.Tag.Performers.Length == 0)
                    mp3.Tag.Performers = new[] { metadata.ArtistUnicode };
                if (string.IsNullOrEmpty(mp3.Tag.Description))
                    mp3.Tag.Description = metadata.Tags;
                mp3.Tag.Comment = $"{mapset.OnlineID} {metadata.Tags}";

                // set beatmap background as album cover 
                if (mp3.Tag.Pictures.Length == 0 && metadata.BackgroundFile is not null)
                {
                    using FileStream? bg = lazerDb.OpenNamedFile(mapset, metadata.BackgroundFile);
                    if (bg is not null)
                    {
                        using MemoryStream ms = new();
                        bg.CopyTo(ms);
                        byte[] image = ms.ToArray();

                        var cover = new TagLib.Id3v2.AttachmentFrame
                        {
                            Type = TagLib.PictureType.FrontCover,
                            Description = "Background",
                            MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
                            Data = image,
                        };
                        mp3.Tag.Pictures = new[] { cover };
                    }
                }

                mp3.Save();
            }
            catch (Exception e)
            {
                metadataFailure?.Invoke(e); // Callback to notify of metadata failure, but export will continue
            }
        }

        public record struct BackgroundExportTask(BeatmapSet OriginSet, BeatmapMetadata BackgroundFile, string OutputFilename);

        /// <summary>
        /// Analyze a BeatmapSet, produding BeatmaepExportTask containers for each unique audio track.
        /// </summary>
        public IEnumerable<BackgroundExportTask> ExtractBackgrounds(BeatmapSet mapset)
        {
            // get beatmap diffs from this set with different background image filenames
            var uniqueMetadata = mapset
                .SelectedBeatmaps
                .Select(b => b.Metadata)
                .Where(m => m.BackgroundFile != null)
                .GroupBy(m => m.BackgroundFile)
                .Select(g => g.First())
                .ToList();

            int beatmapBackgroundCount = 0;
            foreach (var metadata in uniqueMetadata)
            {
                // get output filename for background including original background name
                string outputFilename = metadata.OutputBackgroundFilename(mapset.OnlineID, beatmapBackgroundCount);
                beatmapBackgroundCount++;
                yield return new BackgroundExportTask(mapset, metadata, outputFilename);
            }
        }

        /// <summary>
        /// Export a single background image from a beatmap.
        /// </summary>
        /// <param name="export">A BackgroundExportTask container, as produced by <see cref="ExportBackground(BackgroundExportTask)"/></param>
        /// <exception cref="Exception">General background file export has failed.</exception>
        public void ExportBackground(BackgroundExportTask export)
        {
            var (mapset, metadata, outputFilename) = export;
            string outputFile = Path.Combine(Configuration.ExportPath, outputFilename);

            using FileStream? background = lazerDb.OpenNamedFile(mapset, metadata.BackgroundFile);
            if (background is null)
                throw new IOException($"Background file {metadata.BackgroundFile} not found in beatmap {mapset.ArchiveFilename()}");

            using FileStream output = File.Open(outputFile, FileMode.CreateNew);
            background.CopyTo(output);
        }

        public IEnumerable<Score> GetSelectedReplays() => SelectedBeatmapSets
            .SelectMany(s => s.SelectedBeatmaps)
            .SelectMany(b => b.Scores);

        /// <summary>
        /// Export a player score replay 
        /// </summary>
        /// <param name="score">The player score to export</param>
        /// <param name="filename">The output filename that will be used. Will be set regardless of success of export and should be used for user feedback.</param>
        /// <exception cref="IOException">The Score export was unsuccessful and an error should be noted to the user.</exception>
        public void ExportReplay(Score score, out string filename)
        {
            filename = score.OutputReplayFilename();
            string outputFile = Path.Combine(Configuration.ExportPath, filename);

            string? replayFile = score.Files.FirstOrDefault()?.File?.Hash;
            if (replayFile == null)
                throw new IOException($"Replay file for {outputFile} does not exist.");

            using FileStream replay = lazerDb.OpenHashedFile(replayFile);
            using FileStream output = File.Open(outputFile, FileMode.CreateNew);
            replay.CopyTo(output);
        }

        /// <summary>
        /// Export a single RealmNamedFileUsage, using its original filename.
        /// </summary>
        public void ExportSingleFile(RealmNamedFileUsage fileUsage)
        {
            var filename = Path.GetFileName(fileUsage.Filename); // exporting a single file with its original filename (but no sub-directories that were in the beatmap structure)
            string exportPath = Path.Combine(Configuration.ExportPath, filename);

            using var export = File.Open(exportPath, FileMode.CreateNew);
            using var file = lazerDb.OpenHashedFile(fileUsage.File.Hash);
            file.CopyTo(export);
        }

        private readonly Regex idCollection = new("#([0-9]+)", RegexOptions.Compiled);

        /// <summary>
        /// Update the set of 'selected' beatmaps by applying all filters from this exporter's Configuration.Filters.
        /// </summary>
        /// <param name="collectionFailure">An optional callback to notify users on a collection filter mismatch.</param>
        public void UpdateSelectedBeatmaps(Action<string>? collectionFailure = null)
        {
            List<string> collFilters = new();
            List<BeatmapFilter> beatmapFilters = new();
            bool negateColl = false;
            foreach (var filter in Configuration.Filters)
            {
                // Validate collection filter requests
                if (filter.Collections is not null)
                {
                    List<string> filteredCollections = new();
                    foreach (var requestedFilter in filter.Collections)
                    {
                        string? targetCollection = null;
                        var match = idCollection.Match(requestedFilter);
                        if (match.Success)
                        {
                            // Find any collection filters that are requested by index
                            var collectionId = int.Parse(match.Groups[1].Value);
                            targetCollection = Collections.FirstOrDefault(c => c.Value.CollectionID == collectionId).Key;
                        }
                        else
                        {
                            var exists = Collections.ContainsKey(requestedFilter);
                            if (exists)
                            {
                                targetCollection = requestedFilter;
                            }
                        }
                        if(targetCollection != null)
                        {
                            filteredCollections.Add(targetCollection);
                        }
                        else
                        {
                            collectionFailure?.Invoke(requestedFilter);
                        }
                    }
                    collFilters.AddRange(filteredCollections);
                    negateColl = filter.Negated;
                }

                else // this filter is not a collection filter
                    beatmapFilters.Add(filter);
            }

            // re-build 'collection' filters to optimize/cache iteration of beatmaps/collections for these
            if (collFilters.Count > 0)
            {
                // build list of beatmap ids from selected filters
                var includedHashes = Collections
                    .Where(c => collFilters.Any(c => c == "-all") switch
                    {
                        true => true,
                        false => collFilters.Any(filter => string.Equals(filter, c.Key, StringComparison.OrdinalIgnoreCase))
                    })
                    .SelectMany(c => c.Value.Beatmaps.Select(b => b.ID))
                    .ToList();

                string desc = string.Join(", ", collFilters);
                BeatmapFilter collFilter = new(desc, negateColl,
                    b => includedHashes.Contains(b.ID),
                    FilterTemplate.Collections);

                // with placeholder collection filters removed, add re-built filter 
                beatmapFilters.Add(collFilter);
            }

            // Apply rebuilt collection filter
            Configuration.Filters = beatmapFilters;

            // compute and cache 'selected' beatmaps based on current filters
            int selectedCount = 0;
            int selectedSetCount = 0;
            List<BeatmapSet> selectedSets = new();
            foreach (var set in AllBeatmapSets)
            {
                var filteredMaps =
                    from map in set.Beatmaps
                    where Configuration.Filters.All(f => f.Includes(map))   
                    select map;
                var selected = filteredMaps.ToList();

                set.SelectedBeatmaps = selected;
                selectedCount += selected.Count;

                // after filtering beatmaps, "selected beatmap sets" will only include sets that STILL have at least 1 beatmap 
                if (selected.Count > 0)
                {
                    selectedSetCount++;
                    selectedSets.Add(set);
                }
            }

            SelectedBeatmapSets = selectedSets;
            SelectedBeatmapSetCount = selectedSetCount;
            SelectedBeatmapCount = selectedCount;
        }
    }
}
