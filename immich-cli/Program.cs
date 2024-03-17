using CommandLine;
using immich_cli;
using OpenAPI;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/log.txt", rollOnFileSizeLimit: true)
    .CreateLogger();


await Parser.Default.ParseArguments<Options>(args)
                   .WithParsedAsync(async o =>
                   {
                       var httpClient = new HttpClient();
                       var client = new ImmichClient(httpClient);
                       client.BaseUrl = o.Url;
                       client.ApiKey = o.Key;
                       var options = new UploadOptionsDto();
                       options.Recursive = true;
                       options.Album = o.Album;
                       var uploadCommand = new UploadCommand(client);
                       await uploadCommand.Run(o.Path, options);
                   });
