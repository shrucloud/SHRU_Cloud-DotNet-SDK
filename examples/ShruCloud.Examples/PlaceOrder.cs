using System;
using System.Threading.Tasks;

namespace ShruCloud.Examples
{
    /// <summary>
    /// Places one order for a service ID and input, then prints the order ID and
    /// its first status.
    /// </summary>
    internal static class PlaceOrder
    {
        internal static async Task<int> RunAsync(int serviceId, string input)
        {
            using var client = Program.CreateClient();
            try
            {
                // Running the same command again while this order is still queued
                // returns the same order instead of charging twice.
                var order = await client.PlaceOrderAsync(serviceId, input).ConfigureAwait(false);
                Console.WriteLine("Order #" + order.OrderId + " - status: " + order.Status);
                Console.WriteLine("Follow it with: dotnet run --project examples/ShruCloud.Examples -- check-status " + order.OrderId);
                return 0;
            }
            catch (ShruException error)
            {
                Console.Error.WriteLine("Order failed: " + error.Message);
                if (error.IsAuthError)
                {
                    Console.Error.WriteLine("Check your Key ID and Secret, and that this machine's IP is on the key's allowlist.");
                }
                return 1;
            }
        }
    }
}
