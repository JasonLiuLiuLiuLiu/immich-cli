using CommandLine;
using immich_cli;
using OpenAPI;

await Parser.Default.ParseArguments<Options>(args)
                   .WithParsedAsync(async o =>
                   {
                       var httpClient = new HttpClient();
                       var client = new ImmichClient(httpClient);
                       client.BaseUrl = o.Url;
                       client.ApiKey = o.Key;
                       var options = new UploadOptionsDto();
                       options.Recursive = true;
                       var uploadCommand = new UploadCommand(client);
                       await uploadCommand.Run([o.Path], options);
                   });
