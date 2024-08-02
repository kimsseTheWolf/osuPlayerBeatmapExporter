using BeatmapExporterCore.Exporters.Lazer;
using BeatmapExporterCore.Exporters.Lazer.LazerDB;
using BeatmapExporterCore.Exporters.Lazer.LazerDB.Schema;
using Realms;
using System.Text.Json;

namespace OsuPlayerExporter
{
    public class Program
    {
        public static LazerDatabase? Locate(string directory)
        {
            string? dbFile = LazerDatabase.GetDatabaseFile(directory);
            if (dbFile is null)
            {
                Console.Write("[Err] Path not found");
                return null;
            }
            return dbFile is not null ? new LazerDatabase(dbFile) : null;
        }

        public static void PrintBeatmapInfo(BeatmapSet mapset, string lazerAppLocation, List<BeatmapInfo> beatmapInfoList)
        {
            // 获取所有具有不同音频文件的 beatmap 元数据
            var uniqueMetadata = mapset
                .SelectedBeatmaps
                .Select(b => b.Metadata)
                .DistinctBy(m => m.AudioFile)
                .ToList();

            foreach (var metadata in uniqueMetadata)
            {
                // 获取歌曲的标题和创作者
                string title = metadata.TitleUnicode ?? metadata.Title;
                string artist = metadata.ArtistUnicode ?? metadata.Artist;

                // 调试信息：输出每个 beatmap 的 metadata
                Console.WriteLine($"Processing beatmap: Title = {title}, Artist = {artist}, AudioFile = {metadata.AudioFile}");

                // 获取音频文件的哈希值和后缀
                var audioFile = mapset.Files.FirstOrDefault(f => f.Filename == metadata.AudioFile);
                string audioFileHash = audioFile?.File.Hash ?? "Unknown";
                string audioFileExtension = audioFile != null ? Path.GetExtension(audioFile.Filename) : "Unknown";
                string audioFilePath = audioFile != null 
                    ? Path.Combine(lazerAppLocation, "files", audioFileHash[0].ToString(), audioFileHash.Substring(0, 2), audioFileHash) 
                    : "Unknown";

                // 获取封面图片文件的哈希值和后缀（如果存在）
                var backgroundFile = metadata.BackgroundFile != null
                    ? mapset.Files.FirstOrDefault(f => f.Filename == metadata.BackgroundFile)
                    : null;
                string backgroundFileHash = backgroundFile?.File.Hash ?? "No background file";
                string backgroundFileExtension = backgroundFile != null ? Path.GetExtension(backgroundFile.Filename) : "Unknown";
                string backgroundFilePath = backgroundFile != null 
                    ? Path.Combine(lazerAppLocation, "files", backgroundFileHash[0].ToString(), backgroundFileHash.Substring(0, 2), backgroundFileHash) 
                    : "Unknown";

                // 创建 BeatmapInfo 对象并添加到列表中
                var beatmapInfo = new BeatmapInfo
                {
                    Title = title,
                    Artist = artist,
                    AudioFilePath = audioFilePath,
                    AudioFileExtension = audioFileExtension,
                    BackgroundFilePath = backgroundFilePath,
                    BackgroundFileExtension = backgroundFileExtension,
                    Hash = audioFileHash // 添加 Hash 属性
                };
                beatmapInfoList.Add(beatmapInfo);
            }
        }

        public static List<AudioItem> ExportLazerMedia(string lazerAppLocation)
        {
            // Create empty list for further output
            List<AudioItem> returnList = new List<AudioItem>();
            List<BeatmapInfo> beatmapInfoList = new List<BeatmapInfo>();

            // Pre-processes to create exporter
            LazerDatabase? database = Locate(lazerAppLocation);
            if (database is null)
            {
                Console.WriteLine("[Err] Unable to open database file!");
                return returnList;
            }

            Realm? realm = database.Open();
            if (realm is null)
            {
                Console.WriteLine("[Err] Unable to open lazer database");
                return returnList;
            }

            List<BeatmapSet> beatmaps = realm.All<BeatmapSet>().ToList();
            List<BeatmapCollection> collections = realm.All<BeatmapCollection>().ToList();

            // Create Exporter
            LazerExporter exporter = new(database, beatmaps, collections);
            Console.WriteLine("[Info] Exporter created successfully. Ready to export");

            // start the process of extracting audio
            var allSets = exporter.AllBeatmapSets;
            foreach (var i in allSets)
            {
                PrintBeatmapInfo(i, lazerAppLocation, beatmapInfoList);
            }

            // 将信息保存到 JSON 文件中
            string jsonString = JsonSerializer.Serialize(beatmapInfoList, new JsonSerializerOptions { WriteIndented = true });
            string outputFilePath = Path.Combine(Directory.GetCurrentDirectory(), "beatmapInfo.json");
            File.WriteAllText(outputFilePath, jsonString);

            return returnList;
        }

        public static void Main(string[] args)
        {
            string lazerAppLocation = ""; // 默认路径

            // 解析命令行参数
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--location" && i + 1 < args.Length)
                {
                    lazerAppLocation = args[i + 1];
                }
                else {
                    Console.WriteLine("[Err] You have to indicate a path");
                }
            }

            ExportLazerMedia(lazerAppLocation);
        }
    }

    public class BeatmapInfo
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string AudioFilePath { get; set; }
        public string AudioFileExtension { get; set; }
        public string BackgroundFilePath { get; set; }
        public string BackgroundFileExtension { get; set; }
        public string Hash { get; set; } // 添加 Hash 属性
    }
}