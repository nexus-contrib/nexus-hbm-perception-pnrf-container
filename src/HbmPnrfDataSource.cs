using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
            _root ??= "Z:" + (Context.ResourceLocator ?? throw new Exception("The resource locator is null"))
                .ToPath();

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
   
        Logger.LogTrace("Nearest file path for {Begin} is {FilePath}", begin, potentialFiles[nearestFileIndex]);

        // for each file
        for (int i = nearestFileIndex; i < potentialFiles.Length - 1; i++)
        {
            var filePath = potentialFiles[i];
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

                    // compute segment read begin and segment read end

                    /*
                     *            result  begin  |          | end
                     *    segment case 1:    [                   ]
                     *                 2:             [          ]
                     *                 3:             [ ]
                     *                 4:    [          ]
                     */
                    var segmentBegin = fileBegin + TimeSpan.FromSeconds(segment.StartTime);
                    var roundedSegmentBegin = RoundDown(segmentBegin, samplePeriod);

                    if (segmentBegin >= end)
                    {
                        Logger.LogTrace("No more segments found for the requested time period, leaving");
                        break;
                    }

                    var segmentEnd = fileBegin + TimeSpan.FromSeconds(segment.EndTime);
                    var roundedSegmentEnd = RoundDown(segmentEnd, samplePeriod);

                    if (segmentEnd < begin)
                    {
                        Logger.LogTrace("This segment does not contain data for the current period, skipping");
                        continue;
                    }

                    var segmentReadBegin = begin > segmentBegin
                        ? begin
                        : segmentBegin;

                    var segmentReadEnd = end < segmentEnd
                        ? end
                        : segmentEnd;

                    var segmentStart = (int)((segmentReadBegin - segmentBegin).Ticks / samplePeriod.Ticks);
                    var block = (int)((segmentReadEnd - segmentReadBegin).Ticks / samplePeriod.Ticks);

                    // read data
                    object dataAsObject;

                    segment.Waveform(
                        DataSourceResultType.DataSourceResultType_Double64, 
                        /* the programming examples of the installed PNRF Toolkit suggest 
                         * to begin with 1 instead of 0 */
                        FirstSample: segmentStart + 1,
                        ResultCount: block, 
                        Reduction: 1, 
                        /* no span support :-( */
                        out dataAsObject);

                    if (dataAsObject is null)
                    {
                        Logger.LogTrace("No data available in segment, skipping");
                        continue;
                    }

                    var data = dataAsObject as double[];

                    // copy to result
                    var resultStart = (int)((segmentReadBegin - begin).Ticks / samplePeriod.Ticks);

                    var slicedResult = MemoryMarshal
                        .Cast<byte, double>(request.Data.Span)
                        .Slice(resultStart);

                    data.CopyTo(slicedResult);

                    request.Status.Span
                        .Slice(resultStart, block)
                        .Fill(1);
                }
            }
        }

        return Task.CompletedTask;
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

        return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromDays(yearDay - 1) + TimeSpan.FromSeconds(utcTime);
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

            /* exact match */
            else
                return mid;
        };

        /* no match, round down */
        return mid > 0 ? mid - 1 : 0;
    }

    private static DateTime RoundDown(DateTime dateTime, TimeSpan timeSpan)
    {
        return new DateTime(dateTime.Ticks - (dateTime.Ticks % timeSpan.Ticks), dateTime.Kind);
    }
}