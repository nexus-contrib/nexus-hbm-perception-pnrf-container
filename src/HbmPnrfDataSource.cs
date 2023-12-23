using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.DataModel;
using Nexus.Extensibility;
using RecordingInterface;
using RecordingLoaders;

namespace Nexus.Sources;

public class HbmPnrfDataSource : SimpleDataSource
{
    record HbmPnrfConfig(string CatalogId, string Title, string DataDirectory);

    private string? _root;
    private PNRFLoader _pnrfLoader = new();
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
        throw new NotImplementedException();
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
                    .WithGroups(group.Name);

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
//                 dataSource.GetUTCTime(out var Year, out var YearDay, out var UTCTime, out var Valid);
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