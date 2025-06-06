using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Nexus.DataModel;
using Nexus.Extensibility;
using RecordingInterface;
using RecordingLoaders;

namespace Nexus.Sources;

// Implementation is based on https://www.hbm.com/fileadmin/mediapool/hbmdoc/technical/i2697.pdf
// See also section E "Sweeps and Segments" for a good introduction about how sweeps work

/// <summary>
/// Settings for the HbmPnrf data source.
/// </summary>
/// <param name="CatalogId">The catalog identifier.</param>
/// <param name="Title">The optional catalog title.</param>
/// <param name="DataDirectory">The relative data directory path.</param>
/// <param name="GlobPattern">The glob pattern to search files.</param>
/// <param name="CatalogSourceFiles">The source files to populate the catalog with resources.</param>
public record HbmPnrfSettings(
    string CatalogId,
    string? Title,
    string DataDirectory,
    string GlobPattern,
    string[]? CatalogSourceFiles
);

[ExtensionDescription(
    "Provides catalogs with HBM PNRF data.",
    "https://github.com/nexus-contrib/nexus-hbm-perception-pnrf-container",
    "https://github.com/nexus-contrib/nexus-hbm-perception-pnrf-container/blob/master/src/HbmPnrfDataSource.cs")]
public class HbmPnrf : SimpleDataSource<HbmPnrfSettings>
{
    private const string ORIGINAL_NAME_KEY = "original-name";

    private const string PNRF_GROUP_KEY = "pnrf-group";

    private const string PNRF_RECORDER_KEY = "pnrf-recorder";

    private static PNRFLoader _pnrfLoader = new();

    private string? _root;

    private string Root
    {
        get
        {
            _root ??= "Z:" + (Context.ResourceLocator ?? throw new Exception("The resource locator is null"))
                .ToPath();

            return _root;
        }
    }

    public override Task<CatalogRegistration[]> GetCatalogRegistrationsAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (path == "/")
            return Task.FromResult(new[]
            {
                new CatalogRegistration(
                    Context.SourceConfiguration.CatalogId,
                    Context.SourceConfiguration.Title
                )
            });

        else
            return Task.FromResult(Array.Empty<CatalogRegistration>());
    }

    public override Task<ResourceCatalog> EnrichCatalogAsync(
        ResourceCatalog catalog,
        CancellationToken cancellationToken
    )
    {
        var filePaths = default(string[]);

        if (Context.SourceConfiguration.CatalogSourceFiles is not null)
        {
            filePaths = Context.SourceConfiguration.CatalogSourceFiles
                .Where(filePath => filePath is not null)
                .Select(filePath => Path.Combine(Root, filePath!))
                .ToArray();
        }
        
        else
        {
            var searchPath = Path.Combine(Root, Context.SourceConfiguration.DataDirectory);

            var firstFilePath = Directory
                .GetFiles(searchPath, Context.SourceConfiguration.GlobPattern, SearchOption.AllDirectories)
                .Order()
                .FirstOrDefault();

            filePaths = firstFilePath is null
                ? []
                : [firstFilePath];
        }

        foreach (var filePath in filePaths)
        {
            var catalogBuilder = new ResourceCatalogBuilder(id: Context.SourceConfiguration.CatalogId);

            AddResources(filePath, catalogBuilder);

            catalog = catalog.Merge(catalogBuilder.Build());
        }

        return Task.FromResult(catalog);
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
                Logger.LogTrace("Open file {FilePath}", filePath);
                recording = _pnrfLoader.LoadRecording(filePath);
                filePathToRecordingMap[filePath] = recording;
            }

            return recording;
        }

        // find and load file
        var searchPath = Path.Combine(Root, Context.SourceConfiguration.DataDirectory);

        // TODO ".Order" works probably only until Recording 999 is reached as it is unclear what happens then
        var potentialFiles = Directory
            .GetFiles(searchPath, Context.SourceConfiguration.GlobPattern, SearchOption.AllDirectories)
            .Order()
            .Where(filePath =>
            {
                /* https://www.hbm.com/fileadmin/mediapool/hbmdoc/technical/i2697.pdf
                 *
                 * "The FromDisk.CanLoadRecording() returns a value between 0 and 100.
                 * A zero value indicates that the loader can definitely not load the requested file,
                 * a value of 100 means the loader can read and interpret the file for sure. A value
                 * in between can be interpreted as a percentage of certainty to do a succesful
                 * load."
                 */

                var canLoad = _pnrfLoader.CanLoadRecording(filePath) > 0;

                if (!canLoad)
                    Logger.LogTrace("PNRF reader indicates it is unable to load file {FilePath}.", filePath);

                return canLoad;
            })
            .ToArray();

        if (!potentialFiles.Any())
        {
            Logger.LogTrace("No files found");
            return Task.CompletedTask;
        }

        var nearestFileIndex = FindNearestFilePath(potentialFiles, begin, LoadRecording);
   
        Logger.LogTrace("Nearest file path for {Begin} is {FilePath}", begin, potentialFiles[nearestFileIndex]);

        // for each file
        for (int i = nearestFileIndex; i < potentialFiles.Length; i++)
        {
            var filePath = potentialFiles[i];
            var fileBegin = GetFileBegin(filePath, LoadRecording);

            Logger.LogTrace($"Current file begin is {fileBegin}");

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
                    .GetStringValue(ORIGINAL_NAME_KEY)!;

                var channelRecorder = request.CatalogItem.Resource.Properties!
                    .GetStringValue(PNRF_RECORDER_KEY)!;

                var channelGroup = request.CatalogItem.Resource.Properties!
                    .GetStringValue(PNRF_GROUP_KEY)!;

                var channel = recording.Channels
                    .Cast<IDataChannel>()
                    .FirstOrDefault(channel => 
                        channel.Name == channelName && 
                        channel.Recorder.Name == channelRecorder &&
                        channel.Recorder.Group.Name == channelGroup
                    );

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
                Logger.LogTrace("Processing {SegmentCount} segment(s)", segments.Count);

                var segmentNumber = 0;

                foreach (var segment in segments.Cast<IDataSegment>())
                {
                    Logger.LogTrace("Processing segment {SegmentNumber}", segmentNumber);
                    segmentNumber++;

                    // validate sample period
                    /* PNRF file may contain numbers like 1.9999999999999998E-05
                     * so we round to 100 ns before further processing
                     */
                    var samplePeriod = TimeSpan.FromSeconds(Math.Round(segment.SampleInterval, 8));

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

                    Logger.LogTrace("The segment contains data from {SegmentBegin} to {SegmentEnd}", segmentBegin, segmentEnd);

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

    private void AddResources(string filePath, ResourceCatalogBuilder catalogBuilder)
    {
        Logger.LogTrace("Open file {FilePath}", filePath);
        var recording = _pnrfLoader.LoadRecording(filePath);

        foreach (var group in recording.Groups.Cast<IDataGroup>())
        {
            foreach (IDataRecorder recorder in group.Recorders)
            {
                foreach (var channel in recorder.Channels.Cast<IDataChannel>())
                {
                    var channelName = $"{group.Name}_{recorder.Name}_{channel.Name}"
                        .Replace(" ", "_")
                        .Replace(".", "_")
                        .Replace("∑", "sum")
                        .Replace("φ", "phi")
                        .Replace("λ", "lambda");

                    Logger.LogDebug("Processing channel {ChannelName}", channelName);

                    // get data source
                    if (channel.ChannelType != DataChannelType.DataChannelType_Analog)
                    {
                        Logger.LogTrace("Channel {ChannelName} is not of type 'analog'. Skipping.", channelName);
                        continue;
                    }

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
                    if (!TryEnforceNamingConvention(channelName, out var resourceId))
                    {
                        Logger.LogDebug("Channel {ChannelName} has an invalid name. Skipping.", channelName);
                        continue;
                    }

                    var resourceBuilder = new ResourceBuilder(id: resourceId)
                        .WithGroups($"{group.Name} - {recorder.Name}")
                        .WithProperty(ORIGINAL_NAME_KEY, channel.Name)
                        .WithProperty(PNRF_GROUP_KEY, group.Name)
                        .WithProperty(PNRF_RECORDER_KEY, recorder.Name);

                    if (!string.IsNullOrWhiteSpace(dataSource.YUnit))
                        resourceBuilder.WithUnit(dataSource.YUnit);

                    // create unique representations
                    var samplePeriods = new HashSet<TimeSpan>();

                    foreach (var segment in segments.Cast<IDataSegment>())
                    {
                        /* PNRF file may contain numbers like 1.9999999999999998E-05
                         * so we round to 100 ns before further processing
                         */
                        samplePeriods.Add(TimeSpan.FromSeconds(Math.Round(segment.SampleInterval, 8)));
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
    }

    public int FindNearestFilePath(string[] filePaths, DateTime beginToFind, Func<string, IRecording> loadRecording)
    {
        var left = 0;
        var right = filePaths.Length - 1;
        var mid = 0;

        while (left <= right)
        {
            mid = left + (right - left) / 2;

            var midBegin = GetFileBegin(filePaths[mid], loadRecording);
            Logger.LogTrace("Begin is {FileBegin}", midBegin);

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

    private static DateTime RoundDown(DateTime dateTime, TimeSpan timeSpan)
    {
        return new DateTime(dateTime.Ticks - (dateTime.Ticks % timeSpan.Ticks), dateTime.Kind);
    }
}