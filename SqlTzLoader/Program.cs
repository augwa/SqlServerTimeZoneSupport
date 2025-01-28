using CommandLine;
using Microsoft.Data.SqlClient;
using Microsoft.Threading;
using NodaTime;
using System.Data;

namespace SqlTzLoader
{
    class Program
    {
        static Options _options;

        static void Main(string[] args)
        {
            var parsed = Parser.Default.ParseArguments<Options>(args);
            if (!parsed.Errors.Any())
            {
                AsyncPump.Run(() => parsed.WithParsedAsync(async options =>
                {
                    _options = options;
                    if (_options.Verbose) Console.WriteLine("ConnectionString: {0}", _options.ConnectionString);

                    await MainAsync(args);
                }));
            }
        }

        static async Task MainAsync(string[] args)
        {
            var tzdb = await CurrentTzdbProvider.LoadAsync();

            var zones = await WriteZonesAsync(tzdb.Ids);

            await WriteLinksAsync(zones, tzdb.Aliases);

            await WriteIntervalsAsync(zones, tzdb);

            await WriteVersion(tzdb.VersionId.Split(' ')[1]);
        }

        private static async Task<IDictionary<string, int>> WriteZonesAsync(IEnumerable<string> zones)
        {
            var dictionary = new Dictionary<string, int>();

            var cs = _options.ConnectionString;
            using (var connection = new SqlConnection(cs))
            {
                var command = new SqlCommand("[Tzdb].[AddZone]", connection) { CommandType = CommandType.StoredProcedure };
                command.Parameters.Add("@Name", SqlDbType.VarChar, 50);

                await connection.OpenAsync();

                foreach (var zone in zones)
                {
                    command.Parameters[0].Value = zone;
                    var id = (int)await command.ExecuteScalarAsync();
                    dictionary.Add(zone, id);
                }

                connection.Close();
            }

            return dictionary;
        }

        private static async Task WriteLinksAsync(IDictionary<string, int> zones, ILookup<string, string> aliases)
        {
            var cs = _options.ConnectionString;
            using (var connection = new SqlConnection(cs))
            {
                var command = new SqlCommand("[Tzdb].[AddLink]", connection) { CommandType = CommandType.StoredProcedure };
                command.Parameters.Add("@LinkZoneId", SqlDbType.Int);
                command.Parameters.Add("@CanonicalZoneId", SqlDbType.Int);

                await connection.OpenAsync();

                foreach (var alias in aliases)
                {
                    var canonicalId = zones[alias.Key];
                    foreach (var link in alias)
                    {
                        command.Parameters[0].Value = zones[link];
                        command.Parameters[1].Value = canonicalId;
                        await command.ExecuteNonQueryAsync();
                    }
                }

                connection.Close();
            }
        }

        private static async Task WriteIntervalsAsync(IDictionary<string, int> zones, CurrentTzdbProvider tzdb)
        {
            var currentUtcYear = DateTime.UtcNow.Year;
            var maxYear = currentUtcYear + 10;
            var maxInstant = new LocalDate(maxYear, 1, 1).AtMidnight().InUtc().ToInstant();

            var links = tzdb.Aliases.SelectMany(x => x).OrderBy(x => x).ToList();

            foreach (var id in tzdb.Ids)
            {
                // Skip noncanonical zones
                if (links.Contains(id))
                    continue;

                using var dt = new DataTable();
                dt.Columns.Add("UtcStart", typeof(DateTime));
                dt.Columns.Add("UtcEnd", typeof(DateTime));
                dt.Columns.Add("LocalStart", typeof(DateTime));
                dt.Columns.Add("LocalEnd", typeof(DateTime));
                dt.Columns.Add("OffsetMinutes", typeof(short));
                dt.Columns.Add("Abbreviation", typeof(string));

                var intervals = tzdb[id].GetZoneIntervals(Instant.MinValue, maxInstant);
                foreach (var interval in intervals)
                {
                    DateTime utcStart = DateTime.MinValue;
                    DateTime utcEnd = DateTime.MaxValue;
                    DateTime localStart = DateTime.MinValue;
                    DateTime localEnd = DateTime.MaxValue;

                    try
                    {
                        utcStart = interval.Start == Instant.MinValue
                            ? DateTime.MinValue
                            : interval.Start.ToDateTimeUtc();
                    }
                    catch (InvalidOperationException e) when (e.Message == "Zone interval extends to the beginning of time")
                    {
                        // no-op
                    }

                    try
                    {
                        utcEnd = interval.End == Instant.MaxValue
                            ? DateTime.MaxValue
                            : interval.End.ToDateTimeUtc();
                    }
                    catch (InvalidOperationException e) when (e.Message == "Zone interval extends to the end of time")
                    {
                        // no-op
                    }

                    try
                    {
                        localStart = utcStart == DateTime.MinValue
                            ? DateTime.MinValue
                            : interval.IsoLocalStart.ToDateTimeUnspecified();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }

                    try
                    {
                        localEnd = utcEnd == DateTime.MaxValue
                            ? DateTime.MaxValue
                            : interval.IsoLocalEnd.ToDateTimeUnspecified();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }


                    var offsetMinutes = (short)interval.WallOffset.ToTimeSpan().TotalMinutes;

                    var abbreviation = interval.Name;

                    if (abbreviation.StartsWith("Etc/"))
                    {
                        abbreviation = abbreviation.Substring(4);
                        if (abbreviation.StartsWith("GMT+"))
                            abbreviation = "GMT-" + abbreviation.Substring(4);
                        else if (abbreviation.StartsWith("GMT-"))
                            abbreviation = "GMT+" + abbreviation.Substring(4);
                    }

                    dt.Rows.Add(utcStart, utcEnd, localStart, localEnd, offsetMinutes, abbreviation);
                }

                var cs = _options.ConnectionString;
                using var connection = new SqlConnection(cs);
                var command = new SqlCommand("[Tzdb].[SetIntervals]", connection)
                {
                    CommandType = CommandType.StoredProcedure
                };
                command.Parameters.AddWithValue("@ZoneId", zones[id]);
                var tvp = command.Parameters.AddWithValue("@Intervals", dt);
                tvp.SqlDbType = SqlDbType.Structured;
                tvp.TypeName = "[Tzdb].[IntervalTable]";

                await connection.OpenAsync();
                await command.ExecuteNonQueryAsync();
                connection.Close();
            }
        }

        private static async Task WriteVersion(string version)
        {
            var cs = _options.ConnectionString;
            using var connection = new SqlConnection(cs);
            var command = new SqlCommand("[Tzdb].[SetVersion]", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@Version", version);

            await connection.OpenAsync();
            await command.ExecuteNonQueryAsync();
            connection.Close();
        }
    }
}
