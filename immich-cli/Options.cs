using CommandLine;
using System.Runtime.InteropServices;

namespace immich_cli
{
    public class Options
    {
        [Option('u',"url",Required =true,HelpText ="api url")]
        public string Url { get; set; }
        [Option('k', "key", Required = true, HelpText = "api key")]
        public string Key { get; set; }
        [Option('p', "path", Required = true, HelpText = "file path")]
        public string Path { get; set; }
        [Option("album",Required =false,Default =false,HelpText ="create album based on path")]
        public bool Album { get; set; }
    }
}
