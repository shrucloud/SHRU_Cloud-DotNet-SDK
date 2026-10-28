using System;
using System.Threading.Tasks;

namespace ShruCloud.Examples
{
    /// <summary>
    /// Usage:
    ///   dotnet run --project examples/ShruCloud.Examples -- sync-services
    ///   dotnet run --project examples/ShruCloud.Examples -- place-order &lt;service_id&gt; &lt;input&gt;
    ///   dotnet run --project examples/ShruCloud.Examples -- check-status &lt;order_id&gt;
    /// </summary>
    internal static class Program
    {
        internal static ShruClient CreateClient() => new ShruClient(
            "https://reseller.example.com",
            "YOUR_KEY_ID",
            "YOUR_SECRET");

        private static async Task<int> Main(string[] args)
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "sync-services":
                    return await SyncServices.RunAsync().ConfigureAwait(false);
                case "place-order" when args.Length == 3 && int.TryParse(args[1], out var serviceId):
                    return await PlaceOrder.RunAsync(serviceId, args[2]).ConfigureAwait(false);
                case "check-status" when args.Length == 2 && long.TryParse(args[1], out var orderId):
                    return await CheckStatus.RunAsync(orderId).ConfigureAwait(false);
                default:
                    Console.Error.WriteLine("Usage: dotnet run --project examples/ShruCloud.Examples -- sync-services");
                    Console.Error.WriteLine("       dotnet run --project examples/ShruCloud.Examples -- place-order <service_id> <input>");
                    Console.Error.WriteLine("       dotnet run --project examples/ShruCloud.Examples -- check-status <order_id>");
                    return 1;
            }
        }
    }
}
