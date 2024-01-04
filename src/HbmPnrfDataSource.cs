using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.DataModel;
using Nexus.Extensibility;
using RecordingInterface;
using RecordingLoaders;

namespace Nexus.Sources;

// Implementation is based on https://www.hbm.com/fileadmin/mediapool/hbmdoc/technical/i2697.pdf
// See also section E "Sweeps and Segments" for a good introduction about how sweeps work

public class HbmPnrfDataSource : SimpleDataSource
{
    record HbmPnrfConfig(string CatalogId, string Title, string DataDirectory);

    private const string OriginalNameKey = "original-name";

    private static PNRFLoader _pnrfLoader = new();

    private string? _root;

    private HbmPnrfConfig? _config;

    private string Root
    {
        get
        {
            _root ??= (Context.ResourceLocator ?? throw new Exception("The resource locator is null"))
                .ToPath()
                .Replace("/var/lib/data/", "Z:/volume1/Daten/raw/");

            return _root;
        }
    }

    private HbmPnrfConfig Config
    {
        get
        {
            if (_config is null)
            {
                var configFilePath = Path.Combine(Root, "config.json");

                if (!File.Exists(configFilePath))
                    throw new Exception($"Configuration file {configFilePath} not found.");

                var jsonString = File.ReadAllText(configFilePath);
                _config = JsonSerializer.Deserialize<HbmPnrfConfig>(jsonString) ?? throw new Exception("config is null");
            }

            return _config;
        }
    }

    public override Task<CatalogRegistration[]> GetCatalogRegistrationsAsync(string path, CancellationToken cancellationToken)
    {
        if (path == "/")
            return Task.FromResult(new[] { new CatalogRegistration(Config.CatalogId, Config.Title) });

        else
            return Task.FromResult(Array.Empty<CatalogRegistration>());
    }

    public override Task<ResourceCatalog> GetCatalogAsync(string catalogId, CancellationToken cancellationToken)
    {
        var catalogBuilder = new ResourceCatalogBuilder(id: Config.CatalogId);
        AddResources(catalogBuilder);

        return Task.FromResult(catalogBuilder.Build());
    }

    public override Task ReadAsync(
        DateTime begin, 
        DateTime end, 
        ReadRequest[] requests, 
        ReadDataHandler readData, 
        IProgress<double> progress, 
        CancellationToken cancellationToken)
    {
        var filePathToRecordingMap = new Dictionary<string, IRecording>();

        IRecording LoadRecording(string filePath)
        {
            if (!filePathToRecordingMap!.TryGetValue(filePath, out var recording))
            {
                recording = _pnrfLoader.LoadRecording(filePath);
                filePathToRecordingMap[filePath] = recording;
            }

            return recording;
        }

        // find and load file
        var searchPath = Path.Combine(Root, Config.DataDirectory);

        // TODO ".Order" works probably only until Recording 999 is reached as it is unclear what happens then
        var potentialFiles = Directory
            .GetFiles(searchPath, "Recording*.pnrf", SearchOption.AllDirectories)
            .Order()
            .ToArray();

        if (!potentialFiles.Any())
        {
            Logger.LogTrace("No files found");
            return Task.CompletedTask;
        }

        var nearestFileIndex = FindNearestFilePath(potentialFiles, begin, LoadRecording);
        var currentBegin = begin;

        Logger.LogTrace("Nearest file path for {Begin} is {FilePath}", begin, potentialFiles[nearestFileIndex]);

        for (int i = nearestFileIndex; i < potentialFiles.Length - 1; i++)
        {
            var filePath = potentialFiles[nearestFileIndex];
            var fileBegin = GetFileBegin(filePath, LoadRecording);

            // this file contains data for a later date: leave loop
            if (fileBegin >= end)
            {
                Logger.LogTrace("No more files found for the requested time period, leaving");
                break;
            }

            Logger.LogDebug("Processing file {FilePath}", filePath);

            var recording = LoadRecording(filePath);

            // for each request
            foreach (var request in requests)
            {
                // find channel
                var channelName = request.CatalogItem.Resource.Properties!
                    .GetStringValue(OriginalNameKey)!;

                var channel = recording.Channels
                    .Cast<IDataChannel>()
                    .FirstOrDefault(channel => channel.Name == channelName);

                if (channel is null)
                {
                    Logger.LogTrace("Channel {ChannelName} not found, skipping", channelName);
                    continue;
                }

                Logger.LogDebug("Processing channel {ChannelName}", channelName);

                // get segments
                /* "The safest option is to go always for the mixed datasource."
                 * See also section E "Sweeps and Segments" for a good introduction about how sweeps work
                 */
                var dataSource = channel.get_DataSource(DataSourceSelect.DataSourceSelect_Mixed);

                /* request all data (simple and conservative approach) */
                var sweepStart = dataSource.Sweeps.StartTime;
                var sweepEnd = dataSource.Sweeps.EndTime;
                object segmentsObject;

                dataSource.Data(sweepStart, sweepEnd, out segmentsObject);

                if (segmentsObject is null)
                {
                    Logger.LogTrace("No segments available, skipping");
                    continue;
                }

                var segments = (IDataSegments)segmentsObject;

                // for each segment
                Logger.LogTrace("Processing {SegmentCount} segments", segments.Count);
                var segmentNumber = 0;

                foreach (var segment in segments.Cast<IDataSegment>())
                {
                    Logger.LogTrace("Processing segment {SegmentNumber}", segmentNumber);
                    segmentNumber++;

                    // validate sample period
                    var samplePeriod = TimeSpan.FromSeconds(segment.SampleInterval);

                    if (samplePeriod != request.CatalogItem.Representation.SamplePeriod)
                    {
                        Logger.LogTrace("No matching sample period available in current segment, skipping");
                        continue;
                    }

                    // get absolute segment begin and end
                    var segmentBegin = fileBegin + TimeSpan.FromSeconds(segment.StartTime);
                    var roundedSegmentBegin = RoundDown(segmentBegin, samplePeriod);

                    var segmentEnd = fileBegin + TimeSpan.FromSeconds(segment.EndTime);
                    var roundedSegmentEnd = RoundDown(segmentEnd, samplePeriod);

                    if (segmentBegin >= end)
                    {
                        Logger.LogTrace("No more segments found for the requested time period, leaving");
                        break;
                    }

                    if (segmentEnd < currentBegin)
                    {
                        Logger.LogTrace("This segment does not contain data for the current period, skipping");
                        continue;
                    }

                    #error continue here and calculate offset + length, set data and status and then add period to "currentBegin" (do we need currentBegin for each channel individually?)

                    // read data
                    /* no span support :-( */
                    object dataAsObject;
                    segment.Waveform(DataSourceResultType.DataSourceResultType_Double64, 1, iCnt, 1, out dataAsObject);

                    if (dataAsObject is null)
                    {
                        Logger.LogTrace("No data available in segment, skipping");
                        continue;
                    }

                    var data = dataAsObject as double[];
                }
            }
        }
    }

    private void AddResources(ResourceCatalogBuilder catalogBuilder)
    {
        var searchPath = Path.Combine(Root, Config.DataDirectory);

        var firstFilePath = Directory
            .GetFiles(searchPath, "Recording*.pnrf", SearchOption.AllDirectories)
            .Order()
            .FirstOrDefault();

        if (firstFilePath is null)
            return;

        var recording = _pnrfLoader.LoadRecording(firstFilePath);

        foreach (var group in recording.Groups.Cast<IDataGroup>())
        {
            foreach (var channel in group.Recording.Channels.Cast<IDataChannel>())
            {
                Logger.LogDebug("Processing channel {ChannelName}", channel.Name);

                // get data source
                // if (channel.ChannelType != DataChannelType.DataChannelType_Analog)
                // {
                //     Logger.LogTrace("Channel {ChannelName} is not of type 'analog'. Skipping.", channel.Name);
                //     continue;
                // }

                // "The safest option is to go always for the mixed datasource."
                var dataSource = channel.get_DataSource(DataSourceSelect.DataSourceSelect_Mixed);

                if (!(
                    dataSource.DataType == DataSourceDataType.DataSourceDataType_AnalogWaveform ||
                    dataSource.DataType == DataSourceDataType.DataSourceDataType_DigitalWaveform
                ))
                {
                    Logger.LogTrace("Data source is of type {DataSourceDataType} instead of 'AnalogWaveform' or 'DigitalWaveform'. Skipping.", dataSource.DataType);
                    continue;
                }

                // get segments
                object segmentsObject;

                dataSource.Data(dataSource.Sweeps.StartTime, dataSource.Sweeps.EndTime, out segmentsObject);

                if (segmentsObject is null)
                {
                    Logger.LogDebug("Channel has no data. Unable to determine sample period. Skipping.");
                    continue;
                }

                var segments = (segmentsObject as IDataSegments)!;

                // create resource builder
                if (!TryEnforceNamingConvention(channel.Name, out var resourceId))
                {
                    Logger.LogDebug("Channel {ChannelName} has an invalid name. Skipping.", channel.Name);
                    continue;
                }

                var resourceBuilder = new ResourceBuilder(id: resourceId)
                    .WithGroups(group.Name)
                    .WithProperty(OriginalNameKey, channel.Name);

                if (!string.IsNullOrWhiteSpace(dataSource.YUnit))
                    resourceBuilder.WithUnit(dataSource.YUnit);

                // create unique representations
                var samplePeriods = new HashSet<TimeSpan>();

                foreach (var segment in segments.Cast<IDataSegment>())
                {
                    samplePeriods.Add(TimeSpan.FromSeconds(segment.SampleInterval));
                }

                foreach (var samplePeriod in samplePeriods)
                {
                    var representation = new Representation(
                        dataType: NexusDataType.FLOAT64,
                        samplePeriod: samplePeriod);

                    resourceBuilder
                        .AddRepresentation(representation);
                }

                // add resource
                catalogBuilder.AddResource(resourceBuilder.Build());
            }
        }
    }

    private static bool TryEnforceNamingConvention(string resourceId, [NotNullWhen(returnValue: true)] out string newResourceId)
    {
        newResourceId = resourceId;
        newResourceId = Resource.InvalidIdCharsExpression.Replace(newResourceId, "");
        newResourceId = Resource.InvalidIdStartCharsExpression.Replace(newResourceId, "");

        return Resource.ValidIdExpression.IsMatch(newResourceId);
    }

    private static DateTime GetFileBegin(string filePath, Func<string, IRecording> loadRecording)
    {
        var recording = loadRecording(filePath);

        // No common file begin property has been found in the API so use the first channel for now
        var firstChannel = recording.Channels.OfType<IDataChannel>().FirstOrDefault() 
            ?? throw new Exception("No channels found.");

        if (firstChannel is null)
            return default;

        // "The safest option is to go always for the mixed datasource."
        var dataSource = firstChannel.get_DataSource(DataSourceSelect.DataSourceSelect_Mixed);

        dataSource.GetUTCTime(out var year, out var yearDay, out var utcTime, out var valid);

        if (!valid)
            throw new Exception("No UTC time available.");

        return new DateTime(year, DateTimeKind.Utc) + TimeSpan.FromDays(yearDay) + TimeSpan.FromSeconds(utcTime);
    }

    public static int FindNearestFilePath(string[] filePaths, DateTime beginToFind, Func<string, IRecording> loadRecording)
    {
        var left = 0;
        var right = filePaths.Length - 1;
        var mid = 0;

        while (left <= right)
        {
            mid = left + (right - left) / 2;
            var midBegin = GetFileBegin(filePaths[mid], loadRecording);
            var compare = beginToFind.CompareTo(midBegin);

            if (compare > 0)
                left = mid + 1;

            else if (compare < 0)
                right = mid - 1;

            else
                break;
        };

        return mid;
    }

    private static DateTime RoundDown(DateTime dateTime, TimeSpan timeSpan)
    {
        return new DateTime(dateTime.Ticks - (dateTime.Ticks % timeSpan.Ticks), dateTime.Kind);
    }
}


// using System;
// using RecordingLoaders;
// using RecordingInterface;
// using System.Text.Json;
// using System.Linq;
// using System.IO;

// namespace m
// {
//     class Program
//     {
//         static void Main(string[] args)
//         {
//             var fromDisk = new PNRFLoader();
//             var recording = fromDisk.LoadRecording(args[0]);


//             Console.WriteLine("Groups");
//             foreach (var item in recording.Groups.Cast<IDataGroup>())
//             {

//                 Console.WriteLine(JsonSerializer.Serialize(item));
//             }

//             Console.WriteLine("Recording:");
//             File.WriteAllText("Z:/home/vincent/Downloads/PNRF Reader/recording.json", JsonSerializer.Serialize(recording));

//             Console.WriteLine("Recorders:");
//             // foreach (var recorder in recording.Recorders.Cast<IDataRecorder>())
//             // {
//             //     File.WriteAllText($"Z:/home/vincent/Downloads/PNRF Reader/recorder{recorder.Name}.json", JsonSerializer.Serialize(recorder));
//             // }

//             Console.WriteLine("Channels:");
//             foreach (var channel in recording.Channels.Cast<IDataChannel>())
//             {
//                 var dataSource = channel.get_DataSource(DataSourceSelect.DataSourceSelect_Mixed);
//                 Console.WriteLine("Name: " + dataSource.Name);
//                 dataSource.GetUTCTime(out var Year, out var YearDay, out var UTCTime, out var Valid); // Valid: boolean is true when UTC time is available.
//                 Console.WriteLine("Year: " + Year);
//                 Console.WriteLine("YearDay: " + YearDay);
//                 Console.WriteLine("UTCTime: " + UTCTime);
//                 Console.WriteLine("Valid: " + Valid);
//                 Console.WriteLine("Real-World UTC Time: " + (new DateTime(Year, 1, 1) + TimeSpan.FromDays(YearDay - 1) + TimeSpan.FromSeconds(UTCTime)));
//                 Console.WriteLine(JsonSerializer.Serialize(dataSource));

//                 foreach (var property in dataSource.Properties.Cast<IProperty>())
//                 {
//                     Console.WriteLine(JsonSerializer.Serialize(property));
//                 }
                
//                 // get start and stop time
//                 var start = dataSource.Sweeps.StartTime;
//                 var end = dataSource.Sweeps.EndTime;

//                 Console.WriteLine("Start time: {0} s, End time: {1} s\n", start, end);
//                 // Console.WriteLine("Press any key to continue or Q to quit.\n");

//                 // var cki = Console.ReadKey(true);
//                 //     if (cki.Key == ConsoleKey.Q)
//                 //         return;

//                 // create data array as object
//                 object segmentsObject;

//                 dataSource.Data(start, end, out segmentsObject);

//                 if (segmentsObject == null)
//                 {
//                     Console.WriteLine("No Data.");
//                     return;
//                 }

//                 // convert object into segment information
//                 var segments = segmentsObject as IDataSegments;


//                 foreach (var segment in segments.Cast<IDataSegment>())
//                 {
//                     var iCnt = segment.NumberOfSamples;
//                     Console.WriteLine("\nSegment: {0} samples\n", iCnt);
//                     Console.WriteLine(JsonSerializer.Serialize(segment));
//                 }

//                 Console.ReadKey();
//             }
        

//             // int iSegIndex = 1;              // segment index
//             // int iCount = mySegments.Count;  // number of segments
//             // if (iCount < 1)
//             // {
//             //     Console.WriteLine("No segments found.\n");
//             //     return;
//             // }

//             // // loop through all available segments
//             // for (iSegIndex = 1; iSegIndex <= iCount; iSegIndex++)
//             // {
//             //     // create a single segment
//             //     IDataSegment mySegment = mySegments[iSegIndex];
//             //     int iCnt = mySegment.NumberOfSamples;
//             //     Console.WriteLine("\nSegment {0}: {1} samples\n", iSegIndex, iCnt);
//             //     Console.WriteLine("Press any key to continue or S to skip");
//             //     cki = Console.ReadKey(true);
//             //     if (cki.Key == ConsoleKey.S)
//             //         continue;

//             //     // create object to hold segment data
//             //     object varData;
//             //     // fetch data
//             //     mySegment.Waveform(DataSourceResultType.DataSourceResultType_Double64, 1, iCnt, 1, out varData);

//             //     if (varData == null)
//             //     {
//             //         Console.WriteLine("No valid data found.");
//             //         return;
//             //     }

//             //     // convert object to actual double values
//             //     double[] dSamples = varData as double[];

//             //     double X0 = mySegment.StartTime;
//             //     double DeltaX = mySegment.SampleInterval;
//             //     double X, Y;

//             //     for (int i = 0; i < dSamples.Length; i++)
//             //     {
//             //         X = X0 + i * DeltaX;
//             //         Y = dSamples[i];
//             //         Console.WriteLine("{0}: X = {1}, Y = {2}", i+1, X, Y);
//             //     }
//             // }
//             // Console.WriteLine("\nDone. Press any key to quit.");
//             // Console.ReadKey();
//         }
//     }
// }
