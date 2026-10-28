using System;
using System.Threading.Tasks;

namespace ShruCloud.Examples
{
    /// <summary>
    /// Checks one order every 5 seconds until it leaves queued/processing, then
    /// prints its final status and reply. A real integration would check on a
    /// schedule instead of waiting like this.
    /// </summary>
    internal static class CheckStatus
    {
        internal static async Task<int> RunAsync(long orderId)
        {
            using var client = Program.CreateClient();
            for (var attempt = 1; attempt <= 60; attempt++)
            {
                OrderStatus status;
                try
                {
                    status = await client.CheckStatusAsync(orderId).ConfigureAwait(false);
                }
                catch (ShruException error)
                {
                    Console.Error.WriteLine("Status check failed: " + error.Message);
                    return 1;
                }
                if (status.Status != "queued" && status.Status != "processing")
                {
                    Console.WriteLine("Order #" + orderId + " finished as: " + status.Status);
                    if (status.Reply.Length > 0)
                    {
                        Console.WriteLine("Reply:\n" + status.Reply);
                    }
                    return 0;
                }
                Console.WriteLine("[" + attempt + "/60] Order #" + orderId + " is still " + status.Status + " - checking again in 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            Console.Error.WriteLine("Gave up after 60 checks - order #" + orderId + " is still in progress.");
            return 1;
        }
    }
}
