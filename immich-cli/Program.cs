using immich_cli;
using OpenAPI;

var url = Environment.GetEnvironmentVariable("immich_url");
var apiKey = Environment.GetEnvironmentVariable("immich_key");
var path = Environment.GetEnvironmentVariable("immich_path");
var httpClient = new HttpClient();
var client = new ImmichClient(httpClient);
client.BaseUrl = url;
client.ApiKey = apiKey;
var options = new UploadOptionsDto();
options.Recursive = true;
options.Concurrency = 1;
var uploadCommand = new UploadCommand(client);
await uploadCommand.Run([path], options);