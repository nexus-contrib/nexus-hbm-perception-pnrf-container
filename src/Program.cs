// Debugging:
// File.AppendAllText("Z:/root/code/out.txt", "your text" + Environment.NewLine);

using Nexus.Remoting;
using Nexus.Sources;

// args
if (args.Length < 2)
    throw new Exception("No argument for address and/or port was specified.");

// get address
var address = args[0];

// get port
int port;

try
{
    port = int.Parse(args[1]);
}
catch (Exception ex)
{
    throw new Exception("The second command line argument must be a valid port number.", ex);
}

var communicator = new RemoteCommunicator(new HbmPnrfDataSource(), address, port);

await communicator.RunAsync();