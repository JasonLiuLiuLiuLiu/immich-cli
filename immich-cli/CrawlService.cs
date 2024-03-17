namespace immich_cli
{
    public class CrawlOptions
    {
        public List<string> PathsToCrawl { get; set; }=new List<string>();
        public bool Recursive { get; set; }
        public bool IncludeHidden { get; set; }
    }

    public class CrawlService
    {
        private readonly List<string> _extensions;

        public CrawlService(List<string> image, List<string> video)
        {
            _extensions = image.Concat(video).ToList();
        }

        public async Task<List<string>> Crawl(CrawlOptions options)
        {
            var mediaFiles = new List<string>();
            foreach (var pathToCrawl in options.PathsToCrawl)
            {
                await GetMediaFiles(pathToCrawl, mediaFiles, _extensions, options);
            }
            return mediaFiles;
        }

        static async Task GetMediaFiles(string fullPath, List<string> mediaFiles, List<string> supportFormats, CrawlOptions options)
        {
            var dir = new DirectoryInfo(fullPath);

            if (options.Recursive)
            {
                foreach (var subDir in dir.GetDirectories())
                {
                    if (!options.IncludeHidden && subDir.Attributes.HasFlag(FileAttributes.Hidden))
                        continue;
                    await GetMediaFiles(subDir.FullName, mediaFiles, supportFormats, options);
                }
            }


            var files = dir.GetFiles();

            foreach (var file in files)
            {
                if (!options.IncludeHidden && file.Attributes.HasFlag(FileAttributes.Hidden))
                    continue;

                var fileFormat = Path.GetExtension(file.FullName);

                if (supportFormats.Contains(fileFormat, StringComparer.CurrentCultureIgnoreCase))
                {
                    mediaFiles.Add(file.FullName);
                }
            }
        }
    }
}
