using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ShruCloud
{
    /// <summary>The reply to <see cref="ShruClient.GetAccountInfoAsync"/>.</summary>
    public sealed class AccountInfo
    {
        internal AccountInfo(JsonElement reply)
        {
            Balance = JsonValues.Decimal(reply, "balance");
            Currency = JsonValues.String(reply, "currency") ?? "USD";
            Email = JsonValues.String(reply, "email") ?? "";
            Raw = reply;
        }

        /// <summary>Your current balance.</summary>
        public decimal Balance { get; }

        /// <summary>The balance's currency code, eg. "USD".</summary>
        public string Currency { get; }

        /// <summary>The email on your account.</summary>
        public string Email { get; }

        /// <summary>The full decoded reply, for any field added after this SDK's release.</summary>
        public JsonElement Raw { get; }
    }

    /// <summary>The reply to <see cref="ShruClient.SyncCatalogAsync"/>: your balance and every service you may order.</summary>
    public sealed class Catalog
    {
        internal Catalog(JsonElement reply)
        {
            Balance = JsonValues.Decimal(reply, "balance");
            Currency = JsonValues.String(reply, "currency") ?? "USD";
            var services = new List<Service>();
            if (reply.TryGetProperty("services", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        services.Add(new Service(item));
                    }
                }
            }
            Services = services.AsReadOnly();
            Raw = reply;
        }

        /// <summary>Your current balance.</summary>
        public decimal Balance { get; }

        /// <summary>The balance's currency code, eg. "USD".</summary>
        public string Currency { get; }

        /// <summary>The services you may order, priced as your account is billed.</summary>
        public IReadOnlyList<Service> Services { get; }

        /// <summary>The full decoded reply, for any field added after this SDK's release.</summary>
        public JsonElement Raw { get; }
    }

    /// <summary>One orderable service from the catalog.</summary>
    public sealed class Service
    {
        internal Service(JsonElement item)
        {
            Id = JsonValues.Int(item, "id");
            Name = JsonValues.String(item, "name") ?? "";
            Group = JsonValues.String(item, "group") ?? "";
            Type = JsonValues.String(item, "type") ?? "fixed";
            Price = JsonValues.Decimal(item, "price");
            Delivery = JsonValues.String(item, "delivery") ?? "";
            InputLabel = JsonValues.String(item, "input_label");
            InputRequired = JsonValues.Bool(item, "input_required");
            if (item.TryGetProperty("quantity", out var quantity) && quantity.ValueKind == JsonValueKind.Object)
            {
                Quantity = new QuantityRange(JsonValues.Int(quantity, "min"), JsonValues.Int(quantity, "max"));
            }
            Bulk = JsonValues.Bool(item, "bulk");
            var fields = new List<ServiceField>();
            if (item.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in list.EnumerateArray())
                {
                    if (field.ValueKind == JsonValueKind.Object)
                    {
                        fields.Add(new ServiceField(field));
                    }
                }
            }
            Fields = fields.AsReadOnly();
            Raw = item;
        }

        /// <summary>The ID to pass to <see cref="ShruClient.PlaceOrderAsync"/>.</summary>
        public int Id { get; }

        /// <summary>The service name.</summary>
        public string Name { get; }

        /// <summary>The group the service is listed under.</summary>
        public string Group { get; }

        /// <summary>"fixed" or "quantity".</summary>
        public string Type { get; }

        /// <summary>The price per order (per unit for a "quantity" service), exactly as your account is billed.</summary>
        public decimal Price { get; }

        /// <summary>The expected delivery time, eg. "Instant" or "1-24h".</summary>
        public string Delivery { get; }

        /// <summary>
        /// What the primary input is (IMEI, SN, ECID, an account ID, ...), or null when this
        /// service takes no primary input. Never assume it means "IMEI".
        /// </summary>
        public string? InputLabel { get; }

        /// <summary>Whether the primary input must be filled in.</summary>
        public bool InputRequired { get; }

        /// <summary>The allowed quantity range, only for a "quantity" service; otherwise null.</summary>
        public QuantityRange? Quantity { get; }

        /// <summary>
        /// True when the service accepts several independent inputs: call
        /// <see cref="ShruClient.PlaceOrderAsync"/> once per value. Only ever true for a "fixed" service.
        /// </summary>
        public bool Bulk { get; }

        /// <summary>Extra fields the order needs.</summary>
        public IReadOnlyList<ServiceField> Fields { get; }

        /// <summary>The full decoded service, for any field added after this SDK's release.</summary>
        public JsonElement Raw { get; }
    }

    /// <summary>The allowed quantity range of a "quantity" service.</summary>
    public sealed class QuantityRange
    {
        internal QuantityRange(int min, int max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>The smallest quantity accepted.</summary>
        public int Min { get; }

        /// <summary>The largest quantity accepted.</summary>
        public int Max { get; }
    }

    /// <summary>An extra field a service needs on its orders.</summary>
    public sealed class ServiceField
    {
        internal ServiceField(JsonElement field)
        {
            Label = JsonValues.String(field, "label") ?? "";
            Key = JsonValues.String(field, "key") ?? "";
            Type = JsonValues.String(field, "type") ?? "input";
            Required = JsonValues.Bool(field, "required");
        }

        /// <summary>The label to show a user.</summary>
        public string Label { get; }

        /// <summary>
        /// The name to use for this field in <see cref="ShruClient.PlaceOrderAsync"/>'s fields -
        /// not necessarily the same string as <see cref="Label"/>.
        /// </summary>
        public string Key { get; }

        /// <summary>"input" or "textarea".</summary>
        public string Type { get; }

        /// <summary>Whether the field must be filled in.</summary>
        public bool Required { get; }
    }

    /// <summary>The reply to <see cref="ShruClient.PlaceOrderAsync"/>.</summary>
    public sealed class PlacedOrder
    {
        internal PlacedOrder(JsonElement reply)
        {
            OrderId = JsonValues.Long(reply, "order_id");
            Status = JsonValues.String(reply, "status") ?? "";
            Raw = reply;
        }

        /// <summary>The order ID, for <see cref="ShruClient.CheckStatusAsync"/>.</summary>
        public long OrderId { get; }

        /// <summary>The order's first status, usually "queued".</summary>
        public string Status { get; }

        /// <summary>The full decoded reply, for any field added after this SDK's release.</summary>
        public JsonElement Raw { get; }
    }

    /// <summary>The reply to <see cref="ShruClient.CheckStatusAsync"/>.</summary>
    public sealed class OrderStatus
    {
        internal OrderStatus(JsonElement reply)
        {
            OrderId = JsonValues.Long(reply, "order_id");
            Status = JsonValues.String(reply, "status") ?? "";
            Reply = JsonValues.String(reply, "reply") ?? "";
            Raw = reply;
        }

        /// <summary>The order ID.</summary>
        public long OrderId { get; }

        /// <summary>One of: queued, processing, completed, rejected, refunded.</summary>
        public string Status { get; }

        /// <summary>The result, always plain text; "" until the order finishes.</summary>
        public string Reply { get; }

        /// <summary>The full decoded reply, for any field added after this SDK's release.</summary>
        public JsonElement Raw { get; }
    }

    internal static class JsonValues
    {
        internal static string? String(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value))
            {
                return null;
            }
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                default:
                    return null;
            }
        }

        internal static decimal Decimal(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value))
            {
                return 0m;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
            return 0m;
        }

        internal static long Long(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value))
            {
                return 0;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
            return 0;
        }

        internal static int Int(JsonElement obj, string name)
        {
            var value = Long(obj, name);
            return value < int.MinValue || value > int.MaxValue ? 0 : (int)value;
        }

        internal static bool Bool(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value))
            {
                return false;
            }
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.Number:
                    return value.TryGetDecimal(out var number) && number != 0;
                case JsonValueKind.String:
                    var text = value.GetString();
                    return text == "1" || string.Equals(text, "true", System.StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }
}
