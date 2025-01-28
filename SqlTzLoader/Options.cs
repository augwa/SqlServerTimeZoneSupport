using CommandLine;

namespace SqlTzLoader
{
    internal class Options
    {
        [Option('c', "connectionString", Required = true, HelpText = "Connectionstring of database to update.")]
        public string ConnectionString { get; set; }

        [Option('v', "verbose", HelpText = "Prints all messages to standard output.")]
        public bool Verbose { get; set; }
    }
}
