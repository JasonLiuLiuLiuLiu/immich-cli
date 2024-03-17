﻿using Newtonsoft.Json;
using OpenAPI;
using ShellProgressBar;
using System;
using System.Security.Cryptography;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace immich_cli
{
    public enum CheckResponseStatus
    {
        ACCEPT,
        REJECT,
        DUPLICATE
    }

    public class Asset
    {
        public string Path { get; }
        public string Id { get; set; }
        public string DeviceAssetId { get; private set; }
        public DateTime? FileCreatedAt { get; private set; }
        public DateTime? FileModifiedAt { get; private set; }
        public string SidecarPath { get; private set; }
        public long? FileSize { get; private set; }
        public string AlbumName { get; set; }

        public Asset(string path)
        {
            Path = path;
        }

        public async Task Prepare()
        {
            var fileInfo = new FileInfo(Path);
            DeviceAssetId = $"{System.IO.Path.GetFileName(Path)}-{fileInfo.Length}".Replace(" ", "");
            FileCreatedAt = fileInfo.LastWriteTime;
            FileModifiedAt = fileInfo.LastWriteTime;
            FileSize = fileInfo.Length;
            AlbumName = ExtractAlbumName();
        }

        public async Task<Dictionary<string, object>> GetUploadFormData()
        {
            if (string.IsNullOrEmpty(DeviceAssetId))
                throw new Exception("Device asset id not set");
            if (!FileCreatedAt.HasValue)
                throw new Exception("File created at not set");
            if (!FileModifiedAt.HasValue)
                throw new Exception("File modified at not set");

            // TODO: Doesn't XMP replace the file extension? Will need investigation
            var sideCarPath = $"{Path}.xmp";
            byte[] sidecarData = null;
            try
            {
                if (File.Exists(sideCarPath))
                    sidecarData = await File.ReadAllBytesAsync(sideCarPath);
            }
            catch { }

            var data = new Dictionary<string, object>
            {
                { "deviceAssetId", DeviceAssetId },
                { "deviceId", "CLI" },
                { "fileCreatedAt", FileCreatedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                { "fileModifiedAt", FileModifiedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                { "isFavorite", false }
            };
            var formData = new Dictionary<string, object>();

            foreach (var entry in data)
                formData.Add(entry.Key, entry.Value);

            if (sidecarData != null)
                formData.Add("sidecarData", sidecarData);

            return formData;
        }

        public async Task Delete()
        {
            File.Delete(Path);
        }

        public async Task<string> Hash()
        {
            using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read))
            using (var sha1 = SHA1.Create())
            {
                var hashBytes = await sha1.ComputeHashAsync(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            }
        }

        private string ExtractAlbumName()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? Path.Split('\\')[^2]
                : Path.Split('/')[^2];
        }
    }

    public class UploadOptionsDto
    {
        public bool Recursive { get; set; } = false;
        public List<string> ExclusionPatterns { get; set; } = new List<string>();
        public bool DryRun { get; set; } = false;
        public bool SkipHash { get; set; } = false;
        public bool Delete { get; set; } = false;
        public bool Album { get; set; } = false;
        public string AlbumName { get; set; } = "";
        public bool IncludeHidden { get; set; } = false;
        public int Concurrency { get; set; } = 4;
    }

    public class UploadCommand
    {
        private readonly ImmichClient api;

        public UploadCommand(ImmichClient api) { this.api = api; }

        public async Task Run(string[] paths, UploadOptionsDto options)
        {

            Console.WriteLine("Crawling for assets...");
            var files = await GetFiles(paths, options);

            if (files.Count == 0)
            {
                Console.WriteLine("No assets found, exiting");
                return;
            }

            var assetsToCheck = files.Select(path => new Asset(path)).ToList();

            var checkResult = await CheckAssets(assetsToCheck, options.Concurrency);

            var totalSizeUploaded = await Upload(checkResult.NewAssets, options);
            var messageStart = options.DryRun ? "Would have" : "Successfully";
            if (checkResult.NewAssets.Count == 0)
            {
                Console.WriteLine("All assets were already uploaded, nothing to do.");
            }
            else
            {
                Console.WriteLine($"{messageStart} uploaded {checkResult.NewAssets.Count} asset{(checkResult.NewAssets.Count == 1 ? "" : "s")} ({totalSizeUploaded})");
            }

            if (options.Album || !string.IsNullOrEmpty(options.AlbumName))
            {
                var albumUpdateResult = await UpdateAlbums(checkResult.NewAssets.Concat(checkResult.DuplicateAssets).ToList(), options);
                Console.WriteLine($"{messageStart} created {albumUpdateResult.CreatedAlbumCount} new album{(albumUpdateResult.CreatedAlbumCount == 1 ? "" : "s")}");
                Console.WriteLine($"{messageStart} updated {albumUpdateResult.UpdatedAssetCount} asset{(albumUpdateResult.UpdatedAssetCount == 1 ? "" : "s")}");
            }

            if (!options.Delete)
            {
                return;
            }

            if (options.DryRun)
            {
                Console.WriteLine("Would now have deleted assets, but skipped due to dry run");
                return;
            }

            //Console.WriteLine("Deleting assets that have been uploaded...");

            //await DeleteAssets(checkResult.NewAssets, options);
        }

        public async Task<(List<Asset> NewAssets, List<Asset> DuplicateAssets, List<Asset> RejectedAssets)> CheckAssets(List<Asset> assetsToCheck, int concurrency)
        {
            foreach (var assets in assetsToCheck.Chunk(concurrency))
            {
                await Task.WhenAll(assets.Select(asset => asset.Prepare()));
            }

            var checkProgress = new ProgressBar(assetsToCheck.Count, "Checking assets");
            var newAssets = new List<Asset>();
            var duplicateAssets = new List<Asset>();
            var rejectedAssets = new List<Asset>();
            try
            {
                foreach (var assets in assetsToCheck.Chunk(concurrency))
                {
                    try
                    {
                        var checkedAssets = await GetStatus(assets);
                        foreach (var checkedAsset in checkedAssets)
                        {
                            if (checkedAsset.Status == CheckResponseStatus.ACCEPT)
                            {
                                newAssets.Add(checkedAsset.Asset);
                            }
                            else if (checkedAsset.Status == CheckResponseStatus.DUPLICATE)
                            {
                                duplicateAssets.Add(checkedAsset.Asset);
                            }
                            else
                            {
                                rejectedAssets.Add(checkedAsset.Asset);
                            }
                            checkProgress.Tick();
                        }
                    }
                    catch (Exception ex)
                    {
                        checkProgress.Tick(ex.Message);
                    }
                }
            }
            finally
            {
                checkProgress.Dispose();
            }

            return (newAssets, duplicateAssets, rejectedAssets);
        }

        public async Task<int> Upload(List<Asset> assetsToUpload, UploadOptionsDto options)
        {
            long totalSize = 0;

            foreach (var asset in assetsToUpload)
            {
                totalSize += asset.FileSize ?? 0;
            }

            if (options.DryRun)
            {
                return (int)totalSize;
            }

            using var uploadProgress = new ProgressBar((int)totalSize, "Uploading assets");

            long totalSizeUploaded = 0;
            try
            {
                foreach (var assets in assetsToUpload.Chunk(options.Concurrency))
                {
                    var ids = await UploadAssets(assets);
                    foreach (var (asset, id) in assets.Zip(ids, (asset, id) => (asset, id)))
                    {
                        asset.Id = id;
                        if (asset.FileSize.HasValue)
                        {
                            totalSizeUploaded += asset.FileSize.Value;
                        }
                        else
                        {
                            Console.WriteLine($"Could not determine file size for {asset.Path}");
                        }
                    }
                    uploadProgress.Tick((int)totalSizeUploaded);
                }
            }
            finally
            {
                uploadProgress.Dispose();
            }

            return (int)totalSizeUploaded;
        }

        public async Task<List<string>> GetFiles(string[] paths, UploadOptionsDto options)
        {
            var inputFiles = new List<string>();
            foreach (var pathArgument in paths)
            {
                var fileStat = new FileInfo(pathArgument);
                if (fileStat.Exists)
                {
                    inputFiles.Add(pathArgument);
                }
            }

            var files = await Crawl(paths, options);
            files.AddRange(inputFiles);
            return files;
        }

        public async Task<List<(Asset Asset, CheckResponseStatus Status)>> GetStatus(Asset[] assets)
        {
            var checkResponse = await CheckHashes(assets);

            var responses = new List<(Asset, CheckResponseStatus)>();
            foreach (var (check, asset) in checkResponse.Zip(assets, (check, asset) => (check, asset)))
            {
                if (!string.IsNullOrEmpty(check.AssetId))
                {
                    asset.Id = check.AssetId;
                }

                if (check.Action == AssetBulkUploadCheckResultAction.Accept)
                {
                    responses.Add((asset, CheckResponseStatus.ACCEPT));
                }
                else if (check.Reason == AssetBulkUploadCheckResultReason.Duplicate)
                {
                    responses.Add((asset, CheckResponseStatus.DUPLICATE));
                }
                else
                {
                    responses.Add((asset, CheckResponseStatus.REJECT));
                }
            }

            return responses;
        }

        public async Task<List<AssetBulkUploadCheckResult>> CheckHashes(Asset[] assetsToCheck)
        {
            var checksums = await Task.WhenAll(assetsToCheck.Select(asset => asset.Hash()));
            var assetBulkUploadCheckDto = new AssetBulkUploadCheckDto
            {
                Assets = assetsToCheck.Zip(checksums, (asset, checksum) => new AssetBulkUploadCheckItem { Id = asset.Path, Checksum = checksum }).ToArray(),
            };
            var checkResponse = await api.CheckBulkUploadAsync(assetBulkUploadCheckDto);
            return checkResponse.Results.ToList();
        }

        public async Task<string[]> UploadAssets(Asset[] assets)
        {
            return await Task.WhenAll(assets.Select(ass => UploadAsset(ass)));
        }

        public async Task<string> UploadAsset(Asset asset)
        {
           
            using var content = new MultipartFormDataContent();

            if (string.IsNullOrEmpty(asset.DeviceAssetId))
                throw new Exception("Device asset id not set");
            if (!asset.FileCreatedAt.HasValue)
                throw new Exception("File created at not set");
            if (!asset.FileModifiedAt.HasValue)
                throw new Exception("File modified at not set");

           
           
            try
            {
                // TODO: Doesn't XMP replace the file extension? Will need investigation
                var sideCarPath = $"{asset.Path}.xmp";
                if (File.Exists(sideCarPath))
                    content.Add(new StreamContent(File.OpenRead(sideCarPath)), "assetData", sideCarPath);
            }
            catch { }

            content.Add(new StringContent(asset.DeviceAssetId), "deviceAssetId");
            content.Add(new StringContent("CLI"), "deviceId");
            content.Add(new StringContent(asset.FileCreatedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")), "fileCreatedAt");
            content.Add(new StringContent(asset.FileModifiedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")), "fileModifiedAt");
            
            
            content.Add(new StreamContent(File.OpenRead(asset.Path)), "assetData", Path.GetFileName(asset.Path));

            var url = api.BaseUrl + "/asset/upload";
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-api-key", api.ApiKey);

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(await response.Content.ReadAsStringAsync());
            }
          
            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<AssetFileUploadResponseDto>(responseBody);
            return result.Id;
        }

        public async Task<string> UploadAsset(Dictionary<string, object> data)
        {
            var url = api.BaseUrl + "/asset/upload";

            using var client = new HttpClient();
            using var content = new MultipartFormDataContent();
            foreach (var (key, value) in data)
            {
                if (value is byte[] byteArray)
                {
                    content.Add(new ByteArrayContent(byteArray), key, key);
                }
                else
                {
                    content.Add(new StringContent(value.ToString()), key);
                }
            }
            client.DefaultRequestHeaders.Add("x-api-key", api.ApiKey);

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(await response.Content.ReadAsStringAsync());
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<Dictionary<string, string>>(responseBody);
            return result["id"];
        }

        private async Task<List<string>> Crawl(string[] paths, UploadOptionsDto options)
        {
            var formatResponse = await api.GetSupportedMediaTypesAsync();
            var crawlService = new CrawlService(formatResponse.Image.ToList(), formatResponse.Video.ToList());

            return await crawlService.Crawl(new CrawlOptions
            {
                PathsToCrawl = paths.ToList(),
                Recursive = options.Recursive,
                IncludeHidden = options.IncludeHidden
            });
        }

        public async Task<(int CreatedAlbumCount, int UpdatedAssetCount)> UpdateAlbums(List<Asset> assets, UploadOptionsDto options)
        {
            if (!string.IsNullOrEmpty(options.AlbumName))
            {
                foreach (var asset in assets)
                {
                    asset.AlbumName = options.AlbumName;
                }
            }

            var existingAlbums = await GetAlbums();
            var assetsToUpdate = assets.Where(asset => !string.IsNullOrEmpty(asset.AlbumName) && !string.IsNullOrEmpty(asset.Id)).ToList();

            var newAlbumsSet = new HashSet<string>();
            foreach (var asset in assetsToUpdate)
            {
                if (!existingAlbums.ContainsKey(asset.AlbumName))
                {
                    newAlbumsSet.Add(asset.AlbumName);
                }
            }

            var newAlbums = newAlbumsSet.ToList();

            if (options.DryRun)
            {
                return (newAlbums.Count, assetsToUpdate.Count);
            }

            using var albumCreationProgress = new ProgressBar(newAlbums.Count, "Creating albums");

            try
            {
                foreach (var albumNames in newAlbums.Chunk(options.Concurrency))
                {
                    var newAlbumIds = await Task.WhenAll(albumNames.Select(albumName => api.CreateAlbumAsync(new CreateAlbumDto { AlbumName = albumName }).ContinueWith(task => task.Result.Id)));

                    foreach (var item in albumNames.Zip(newAlbumIds, (albumName, albumId) => (albumName, albumId)))
                    {
                        existingAlbums[item.albumName] = item.albumId;
                    }

                    albumCreationProgress.Tick(albumNames.Count());
                }
            }
            finally
            {
                albumCreationProgress.Dispose();
            }

            var albumToAssets = new Dictionary<string, List<string>>();
            foreach (var asset in assetsToUpdate)
            {
                var albumId = existingAlbums.GetValueOrDefault(asset.AlbumName);
                if (albumId != null)
                {
                    if (!albumToAssets.ContainsKey(albumId))
                    {
                        albumToAssets[albumId] = new List<string>();
                    }
                    albumToAssets[albumId].Add(asset.Id);
                }
            }

            using var albumUpdateProgress = new ProgressBar(assetsToUpdate.Count, "Adding assets to albums");


            try
            {
                foreach (var item in albumToAssets)
                {
                    foreach (var assetBatch in assets.Chunk(Math.Min(1000 * (options.Concurrency), 65_000)))
                    {
                        await api.AddAssetsToAlbumAsync(new Guid(item.Key), null, new BulkIdsDto { Ids = assetBatch.Select(u => new Guid(u.Id)).ToArray() });
                        albumUpdateProgress.Tick(assetBatch.Count());
                    }
                }
            }
            finally
            {
                albumUpdateProgress.Dispose();
            }

            return (newAlbums.Count, assetsToUpdate.Count);
        }

        public async Task<Dictionary<string, string>> GetAlbums()
        {
            var existingAlbums = await api.GetAllAlbumsAsync(null, null);

            var albumMapping = new Dictionary<string, string>();
            foreach (var album in existingAlbums)
            {
                albumMapping.Add(album.AlbumName, album.Id);
            }

            return albumMapping;
        }

    }
}