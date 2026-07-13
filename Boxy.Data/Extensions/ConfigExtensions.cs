using System.Text.Json;
using Boxy.Data.Entities;

namespace Boxy.Data.Extensions;

/// <summary>
/// Typed access to the <see cref="Config"/> key/value store: one JSON row per settings POCO,
/// keyed by its type name. Read with <see cref="GetSettingsAsync{T}"/>, write with
/// <see cref="SaveSettingsAsync{T}"/>.
/// </summary>
public static class ConfigExtensions
{
    private static string KeyFor<T>()
    {
        return typeof(T).Name;
    }

    /// <summary>True once settings for <typeparamref name="T"/> have been saved (a row exists), so callers
    /// can distinguish "configured in-app" from "never set" (and fall back elsewhere).</summary>
    public static async Task<bool> HasSettingsAsync<T>(this AppDbContext db, CancellationToken ct = default)
    {
        return await db.Configs.AsNoTracking().AnyAsync(c => c.Id == KeyFor<T>(), ct);
    }

    /// <summary>The stored settings for <typeparamref name="T"/>, or a fresh default when unset.</summary>
    public static async Task<T> GetSettingsAsync<T>(this AppDbContext db, CancellationToken ct = default)
        where T : new()
    {
        var key = KeyFor<T>();
        var row = await db.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == key, ct);
        return row is null ? new T() : JsonSerializer.Deserialize<T>(row.Value) ?? new T();
    }

    /// <summary>Upsert the settings for <typeparamref name="T"/> and save.</summary>
    public static async Task SaveSettingsAsync<T>(this AppDbContext db, T value, CancellationToken ct = default)
    {
        var key = KeyFor<T>();
        var json = JsonSerializer.Serialize(value);
        var row = await db.Configs.FirstOrDefaultAsync(c => c.Id == key, ct);
        if (row is null)
        {
            db.Configs.Add(new Config { Id = key, Value = json });
        }
        else
        {
            row.Value = json;
        }

        await db.SaveChangesAsync(ct);
    }
}
