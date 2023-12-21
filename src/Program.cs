using System;
using RecordingLoaders;
using RecordingInterface;
using System.Text.Json;
using System.Linq;
using System.IO;

namespace m
{
    class Program
    {
        static void Main(string[] args)
        {
            var fromDisk = new PNRFLoader();
            var fileName = @"Z:/home/vincent/Downloads/pnrf/Recording001.pnrf";
            var recording = fromDisk.LoadRecording(fileName);


            Console.WriteLine("Groups");
            foreach (var item in recording.Groups.Cast<IDataGroup>())
            {

                Console.WriteLine(JsonSerializer.Serialize(item));
            }

            Console.WriteLine("Recording:");
            File.WriteAllText("Z:/home/vincent/Downloads/PNRF Reader/recording.json", JsonSerializer.Serialize(recording));

            Console.WriteLine("Recorders:");
            // foreach (var recorder in recording.Recorders.Cast<IDataRecorder>())
            // {
            //     File.WriteAllText($"Z:/home/vincent/Downloads/PNRF Reader/recorder{recorder.Name}.json", JsonSerializer.Serialize(recorder));
            // }

            Console.WriteLine("Channels:");
            foreach (var channel in recording.Channels.Cast<IDataChannel>())
            {
                var dataSource = channel.get_DataSource(DataSourceSelect.DataSourceSelect_Mixed);
                Console.WriteLine("Name: " + dataSource.Name);
                dataSource.GetUTCTime(out var Year, out var YearDay, out var UTCTime, out var Valid);
                Console.WriteLine("Year: " + Year);
                Console.WriteLine("YearDay: " + YearDay);
                Console.WriteLine("UTCTime: " + UTCTime);
                Console.WriteLine("Valid: " + Valid);
                Console.WriteLine("Real-World UTC Time: " + (new DateTime(Year, 1, 1) + TimeSpan.FromDays(YearDay - 1) + TimeSpan.FromSeconds(UTCTime)));
                Console.WriteLine(JsonSerializer.Serialize(dataSource));

                foreach (var property in dataSource.Properties.Cast<IProperty>())
                {
                    Console.WriteLine(JsonSerializer.Serialize(property));
                }
                
                // get start and stop time
                var start = dataSource.Sweeps.StartTime;
                var end = dataSource.Sweeps.EndTime;

                Console.WriteLine("Start time: {0} s, End time: {1} s\n", start, end);
                // Console.WriteLine("Press any key to continue or Q to quit.\n");

                // var cki = Console.ReadKey(true);
                //     if (cki.Key == ConsoleKey.Q)
                //         return;

                // create data array as object
                object segmentsObject;

                dataSource.Data(start, end, out segmentsObject);

                if (segmentsObject == null)
                {
                    Console.WriteLine("No Data.");
                    return;
                }

                // convert object into segment information
                var segments = segmentsObject as IDataSegments;


                foreach (var segment in segments.Cast<IDataSegment>())
                {
                    var iCnt = segment.NumberOfSamples;
                    Console.WriteLine("\nSegment: {0} samples\n", iCnt);
                    Console.WriteLine(JsonSerializer.Serialize(segment));
                }

                Console.ReadKey();
            }
        

            // int iSegIndex = 1;              // segment index
            // int iCount = mySegments.Count;  // number of segments
            // if (iCount < 1)
            // {
            //     Console.WriteLine("No segments found.\n");
            //     return;
            // }

            // // loop through all available segments
            // for (iSegIndex = 1; iSegIndex <= iCount; iSegIndex++)
            // {
            //     // create a single segment
            //     IDataSegment mySegment = mySegments[iSegIndex];
            //     int iCnt = mySegment.NumberOfSamples;
            //     Console.WriteLine("\nSegment {0}: {1} samples\n", iSegIndex, iCnt);
            //     Console.WriteLine("Press any key to continue or S to skip");
            //     cki = Console.ReadKey(true);
            //     if (cki.Key == ConsoleKey.S)
            //         continue;

            //     // create object to hold segment data
            //     object varData;
            //     // fetch data
            //     mySegment.Waveform(DataSourceResultType.DataSourceResultType_Double64, 1, iCnt, 1, out varData);

            //     if (varData == null)
            //     {
            //         Console.WriteLine("No valid data found.");
            //         return;
            //     }

            //     // convert object to actual double values
            //     double[] dSamples = varData as double[];

            //     double X0 = mySegment.StartTime;
            //     double DeltaX = mySegment.SampleInterval;
            //     double X, Y;

            //     for (int i = 0; i < dSamples.Length; i++)
            //     {
            //         X = X0 + i * DeltaX;
            //         Y = dSamples[i];
            //         Console.WriteLine("{0}: X = {1}, Y = {2}", i+1, X, Y);
            //     }
            // }
            // Console.WriteLine("\nDone. Press any key to quit.");
            // Console.ReadKey();
        }
    }
}
