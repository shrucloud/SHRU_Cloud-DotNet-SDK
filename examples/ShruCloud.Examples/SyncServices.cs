using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;

namespace ShruCloud.Examples
{
    /// <summary>
    /// Prints your balance and the full service catalog as JSON: every field
    /// PlaceOrderAsync() can need (input label, quantity range, extra fields).
    /// </summary>
    internal static class SyncServices
    {
        internal static async Task<int> RunAsync()
        {
            using var client = Program.CreateClient();
            try
            {
                var catalog = await client.SyncCatalogAsync().ConfigureAwait(false);
                using (var stdout = Console.OpenStandardOutput())
                using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) }))
                {
                    catalog.Raw.WriteTo(writer);
                }
                Console.WriteLine();
                Console.Error.WriteLine(
                    "\n" + catalog.Services.Count.ToString(CultureInfo.InvariantCulture) + " service(s) - balance: " +
                    catalog.Balance.ToString("0.00", CultureInfo.InvariantCulture) + " " + catalog.Currency);
                return 0;
            }
            catch (ShruException error)
            {
                Console.Error.WriteLine("Sync failed: " + error.Message);
                return 1;
            }
        }
    }
}
